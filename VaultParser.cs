using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BwPicker;

sealed record ParsedVault(List<Entry> Entries, Dictionary<string, (string? Username, MemorySecret? Password)> Credentials) : IDisposable
{
    public void Dispose() { foreach (var credential in Credentials.Values) credential.Password?.Dispose(); }
}

static class VaultParser
{
    public static ParsedVault Parse(ReadOnlySpan<byte> json)
    {
        var parsed = new ParsedVault([], []);
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            bool ended = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) { ended = true; break; }
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
                ReadItem(ref reader, parsed);
            }
            if (!ended || reader.Read()) throw new JsonException();
            return parsed;
        }
        catch (JsonException) { parsed.Dispose(); throw new InvalidOperationException("Bitwarden returned an invalid vault response."); }
        catch { parsed.Dispose(); throw; }
    }

    static void ReadItem(ref Utf8JsonReader reader, ParsedVault parsed)
    {
        string? id = null, name = null, username = null;
        int type = 0;
        MemorySecret? password = null;
        List<string> uris = [];
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                string property = reader.GetString()!;
                if (!reader.Read()) throw new JsonException();
                switch (property)
                {
                    case "id": id = String(ref reader); break;
                    case "name": name = String(ref reader); break;
                    case "type": if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out type)) throw new JsonException(); break;
                    case "login" when reader.TokenType == JsonTokenType.StartObject:
                        password?.Dispose(); password = null; uris.Clear();
                        ReadLogin(ref reader, out username, out password, uris); break;
                    default: reader.Skip(); break;
                }
            }
            if (type != 1) return;
            if (parsed.Entries.Count >= 50_000) throw new InvalidOperationException("The vault exceeds the supported login count.");
            if (string.IsNullOrEmpty(id) || parsed.Credentials.ContainsKey(id)) throw new JsonException();
            parsed.Entries.Add(new Entry(id, name ?? "", username, uris.ToArray()));
            parsed.Credentials.Add(id, (username, password)); password = null;
        }
        finally { password?.Dispose(); }
    }

    static void ReadLogin(ref Utf8JsonReader reader, out string? username, out MemorySecret? password, List<string> uris)
    {
        username = null; password = null;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                string property = reader.GetString()!;
                if (!reader.Read()) throw new JsonException();
                switch (property)
                {
                    case "username": username = String(ref reader); break;
                    case "password":
                        password?.Dispose(); password = null;
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 1024 * 1024) throw new JsonException();
                        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, reader.ValueSpan.Length));
                        try { int length = reader.CopyString(bytes.AsSpan()); password = new MemorySecret(bytes.AsSpan(0, length)); }
                        finally { CryptographicOperations.ZeroMemory(bytes); ArrayPool<byte>.Shared.Return(bytes); }
                        break;
                    case "uris" when reader.TokenType == JsonTokenType.StartArray:
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                                bool isUri = reader.ValueTextEquals("uri");
                                if (!reader.Read()) throw new JsonException();
                                if (isUri && String(ref reader) is { } uri) uris.Add(uri); else reader.Skip();
                            }
                        }
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch { password?.Dispose(); password = null; throw; }
    }

    static string? String(ref Utf8JsonReader reader)
    {
        if (reader.ValueSpan.Length > 32_768) throw new JsonException();
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(), JsonTokenType.Null => null, _ => throw new JsonException(),
        };
    }
}
