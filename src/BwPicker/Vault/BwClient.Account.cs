using System.Text.Json;

namespace BwPicker;

enum ServerKind { BitwardenUs, BitwardenEu, SelfHosted }

/// <summary>Which Bitwarden server the CLI talks to. The CLI treats "bitwarden.com" as the US cloud.</summary>
sealed record ServerChoice(ServerKind Kind, string? Url = null)
{
    public const string EuUrl = "https://vault.bitwarden.eu";

    public static ServerChoice FromStatus(string? serverUrl)
    {
        if (string.IsNullOrEmpty(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return new(ServerKind.BitwardenUs);
        string host = uri.Host.ToLowerInvariant();
        if (host is "bitwarden.com" or "vault.bitwarden.com") return new(ServerKind.BitwardenUs);
        if (host is "bitwarden.eu" or "vault.bitwarden.eu") return new(ServerKind.BitwardenEu);
        return new(ServerKind.SelfHosted, serverUrl.TrimEnd('/'));
    }

    /// <summary>Accepts "vault.example.com" or a full URL; only HTTPS without embedded credentials.</summary>
    public static ServerChoice SelfHosted(string input)
    {
        string text = input.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Enter your server's address.");
        if (!text.Contains("://")) text = "https://" + text;
        BwClient.ValidateServer(text);
        var uri = new Uri(text);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidOperationException("Enter the server address without ? or # parts.");
        return FromStatus(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    public string CliValue => Kind switch
    {
        ServerKind.BitwardenUs => "bitwarden.com",
        ServerKind.BitwardenEu => EuUrl,
        _ => Url!,
    };

    public string DisplayName => Kind switch
    {
        ServerKind.BitwardenUs => "bitwarden.com",
        ServerKind.BitwardenEu => "bitwarden.eu",
        _ => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url ?? "",
    };

    public bool SameAs(ServerChoice other) =>
        Kind == other.Kind && (Kind != ServerKind.SelfHosted || string.Equals(Url, other.Url, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Values match the CLI's --method numbers.</summary>
enum TwoStepMethod { Authenticator = 0, Email = 1, YubiKey = 3 }

enum SignInOutcome { SignedIn, NeedsCode, NeedsMethod, NeedsUnlock }

sealed partial class BwClient
{
    public async Task SetServer(ServerChoice server)
    {
        var account = await Status();
        if (account.Status != "unauthenticated") throw new InvalidOperationException("Sign out before switching servers.");
        using var output = await Run(null, null, "config", "server", server.CliValue, "--response");
        var (ok, message) = ReadResponse(output);
        InvalidateStatus();
        if (!ok) throw new InvalidOperationException(message ?? "Could not change the server.");
    }

    public async Task SignOut()
    {
        await Lock();
        using var output = await Run(null, null, "logout", "--response");
        var (ok, message) = ReadResponse(output);
        InvalidateStatus();
        if (!ok && message?.Contains("not logged in", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException(message ?? "Could not sign out.");
    }

    /// <summary>
    /// Email and master password, plus a two-step code when the account needs one. A successful password
    /// sign-in also unlocks the vault. The CLI cannot answer Bitwarden's new-device email check without
    /// an interactive prompt; API key sign-in avoids it.
    /// </summary>
    public async Task<SignInOutcome> SignIn(string email, MemorySecret masterPassword, TwoStepMethod? method = null, string? code = null)
    {
        lock (state) EnsureAvailable();
        email = email.Trim();
        if (email.Length == 0 || !email.Contains('@')) throw new InvalidOperationException("Enter your account email.");
        if (code != null && (code.Length > 64 || code.Any(char.IsControl))) throw new InvalidOperationException("That code doesn't look right.");

        var args = new List<string> { "login", email, "--passwordenv", "BWPICKER_PW" };
        if (method is { } m) args.AddRange(["--method", ((int)m).ToString()]);
        if (!string.IsNullOrWhiteSpace(code)) args.AddRange(["--code", code.Trim()]);
        args.Add("--response");

        using var output = await Run([("BWPICKER_PW", masterPassword)], null, [.. args]);
        var (ok, message) = ReadResponse(output);
        InvalidateStatus();
        if (!ok)
        {
            if (message == "Login failed. No provider selected.") return SignInOutcome.NeedsMethod;
            if (message == "Code is required.")
            {
                if (string.IsNullOrWhiteSpace(code)) return SignInOutcome.NeedsCode;
                throw new InvalidOperationException(
                    "Bitwarden asked for a new-device email code, which the CLI can only take interactively. Sign in with an API key instead.");
            }
            throw new InvalidOperationException(message ?? "Sign-in failed.");
        }

        using var next = ReadSessionFromResponse(output);
        if (next == null) return SignInOutcome.NeedsUnlock;
        lock (state)
        {
            EnsureAvailable();
            session?.Dispose();
            using var lease = next.Reveal();
            session = new MemorySecret(lease.Characters);
        }
        return SignInOutcome.SignedIn;
    }

    /// <summary>Personal API key from the web vault. Skips two-step and new-device checks; the vault stays locked.</summary>
    public async Task<SignInOutcome> SignInWithApiKey(MemorySecret clientId, MemorySecret clientSecret)
    {
        lock (state) EnsureAvailable();
        using var output = await Run([("BW_CLIENTID", clientId), ("BW_CLIENTSECRET", clientSecret)], null, "login", "--apikey", "--response");
        var (ok, message) = ReadResponse(output);
        InvalidateStatus();
        if (!ok) throw new InvalidOperationException(message ?? "Sign-in failed.");
        return SignInOutcome.NeedsUnlock;
    }

    void InvalidateStatus()
    {
        lock (state) { statusTask = null; CachedStatus = null; }
    }

    /// <summary>`--response` output: {"success":bool,"message":"…","data":{…}}. Messages are not secret.</summary>
    internal static (bool Ok, string? Message) ReadResponse(CliOutput output)
    {
        var span = output.Stdout.Memory.Span;
        int start = span.IndexOf((byte)'{');
        if (start < 0) return (output.Code == 0, null);
        try
        {
            using var doc = JsonDocument.Parse(output.Stdout.Memory[start..]);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            string? message = GetString(root, "message");
            if (message == null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                message = GetString(data, "title");
            return (ok, message);
        }
        catch (JsonException) { return (false, "Bitwarden returned an unexpected response."); }
    }

    /// <summary>Reads data.raw (the session key) straight from the buffer, without creating a string.</summary>
    internal static MemorySecret? ReadSessionFromResponse(CliOutput output)
    {
        var span = output.Stdout.Memory.Span;
        int start = span.IndexOf((byte)'{');
        if (start < 0) return null;
        var reader = new Utf8JsonReader(span[start..]);
        int depth = 0;
        bool inData = false;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: depth++; break;
                    case JsonTokenType.EndObject: depth--; if (depth < 2) inData = false; break;
                    case JsonTokenType.PropertyName when depth == 1 && reader.ValueTextEquals("data"u8):
                        inData = true; break;
                    case JsonTokenType.PropertyName when depth == 2 && inData && reader.ValueTextEquals("raw"u8):
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped) return null;
                        return reader.ValueSpan.Length == 0 ? null : ParseSession(reader.ValueSpan);
                }
            }
        }
        catch (JsonException) { }
        return null;
    }
}
