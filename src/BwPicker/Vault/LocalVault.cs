using System.Security.Cryptography;
using System.Text.Json;

namespace BwPicker;

/// <summary>The local vault can't be opened in-process; the caller falls back to the Bitwarden CLI.</summary>
sealed class LocalVaultUnsupportedException(string reason) : Exception(reason);

/// <summary>
/// Reads the Bitwarden CLI's local state (data.json), where the vault is stored end-to-end encrypted,
/// and decrypts it in-process the way the browser extension does. Read-only: the CLI stays responsible
/// for signing in and syncing. Only PBKDF2 personal vaults are handled; anything else (Argon2id,
/// organization items, Key Connector) throws <see cref="LocalVaultUnsupportedException"/>.
/// </summary>
static class LocalVault
{
    const long MaxFileBytes = 256L * 1024 * 1024;

    public sealed record Account(string Salt, int Iterations, string WrappedUserKey);

    public static string DefaultPath
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("BITWARDENCLI_APPDATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bitwarden CLI");
            return Path.Combine(dir, "data.json");
        }
    }

    public static Account ReadAccount(string path) => WithDocument(path, root =>
    {
        string userId = ActiveUser(root);
        if (root.TryGetProperty($"user_{userId}_keyConnector_usesKeyConnector", out var kc) && kc.ValueKind == JsonValueKind.True)
            throw new LocalVaultUnsupportedException("Key Connector accounts unlock through the CLI.");

        string? salt = null, wrapped = null;
        JsonElement kdf = default;
        if (root.TryGetProperty($"user_{userId}_masterPasswordUnlock_masterPasswordUnlockKey", out var unlock) &&
            unlock.ValueKind == JsonValueKind.Object)
        {
            salt = Text(unlock, "salt");
            wrapped = Text(unlock, "masterKeyWrappedUserKey");
            unlock.TryGetProperty("kdf", out kdf);
        }
        // Older CLI state layout.
        if (wrapped == null && root.TryGetProperty($"user_{userId}_masterPassword_masterKeyEncryptedUserKey", out var legacy) &&
            legacy.ValueKind == JsonValueKind.String)
            wrapped = legacy.GetString();
        if (kdf.ValueKind != JsonValueKind.Object)
            root.TryGetProperty($"user_{userId}_kdfConfig_kdfConfig", out kdf);
        if (salt == null && root.TryGetProperty("global_account_accounts", out var accounts) &&
            accounts.TryGetProperty(userId, out var account))
            salt = Text(account, "email")?.Trim().ToLowerInvariant();

        if (kdf.ValueKind != JsonValueKind.Object || salt == null || wrapped == null)
            throw new LocalVaultUnsupportedException("The local vault has no master password unlock data.");
        if (!kdf.TryGetProperty("kdfType", out var type) || type.GetInt32() != 0)
            throw new LocalVaultUnsupportedException("Only PBKDF2 accounts unlock in-process.");
        if (!kdf.TryGetProperty("iterations", out var iterations) || !iterations.TryGetInt32(out int count))
            throw new LocalVaultUnsupportedException("The local vault has no KDF iteration count.");
        return new Account(salt, count, wrapped);
    });

    /// <summary>
    /// Derives the master key and unwraps the 64-byte user key into a pinned array the caller must zero.
    /// A wrong master password fails authentication (<see cref="BitwardenCrypto.AuthenticationFailedException"/>).
    /// </summary>
    public static byte[] UnlockUserKey(Account account, ReadOnlySpan<char> masterPassword)
    {
        var stretched = BitwardenCrypto.DeriveStretchedMasterKey(masterPassword, account.Salt, account.Iterations);
        try
        {
            var userKey = BitwardenCrypto.Decrypt(account.WrappedUserKey, stretched);
            if (userKey.Length != BitwardenCrypto.KeyLength)
            {
                CryptographicOperations.ZeroMemory(userKey);
                throw new LocalVaultUnsupportedException("Unexpected user key format.");
            }
            return userKey;
        }
        finally { CryptographicOperations.ZeroMemory(stretched); }
    }

    /// <summary>Decrypts the active account's logins (not in trash). Passwords go straight into protected memory.</summary>
    public static ParsedVault ReadVault(string path, ReadOnlySpan<byte> userKey)
    {
        var key = userKey.ToArray(); // the lambda can't capture a span; zeroed below
        try
        {
            return WithDocument(path, root =>
            {
                string userId = ActiveUser(root);
                if (!root.TryGetProperty($"user_{userId}_ciphers_ciphers", out var ciphers) || ciphers.ValueKind != JsonValueKind.Object)
                    return new ParsedVault([], []);
                var parsed = new ParsedVault([], []);
                try
                {
                    foreach (var item in ciphers.EnumerateObject())
                        ReadItem(item.Value, key, parsed);
                    return parsed;
                }
                catch { parsed.Dispose(); throw; }
            });
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    static void ReadItem(JsonElement cipher, byte[] userKey, ParsedVault parsed)
    {
        if (Text(cipher, "organizationId") != null)
            throw new LocalVaultUnsupportedException("Organization items unlock through the CLI.");
        if (!cipher.TryGetProperty("type", out var type) || type.GetInt32() != 1) return;
        if (Text(cipher, "deletedDate") != null) return; // in trash, like `bw list items`
        string id = Text(cipher, "id") ?? throw new LocalVaultUnsupportedException("A login has no id.");
        if (parsed.Credentials.ContainsKey(id) || parsed.Entries.Count >= 50_000)
            throw new LocalVaultUnsupportedException("The local vault looks inconsistent.");

        byte[]? itemKey = Text(cipher, "key") is { } wrappedItemKey ? BitwardenCrypto.Decrypt(wrappedItemKey, userKey) : null;
        try
        {
            if (itemKey != null && itemKey.Length != BitwardenCrypto.KeyLength)
                throw new LocalVaultUnsupportedException("Unexpected item key format.");
            var key = itemKey ?? userKey;
            string name = Text(cipher, "name") is { } n ? BitwardenCrypto.DecryptString(n, key) : "";
            string? username = null;
            MemorySecret? password = null;
            var uris = new List<string>();
            try
            {
                if (cipher.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.Object)
                {
                    if (Text(login, "username") is { } u) username = BitwardenCrypto.DecryptString(u, key);
                    if (Text(login, "password") is { } p)
                    {
                        var plain = BitwardenCrypto.Decrypt(p, key);
                        try { password = new MemorySecret(plain); }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    if (login.TryGetProperty("uris", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var uri in list.EnumerateArray())
                            if (Text(uri, "uri") is { } encrypted) uris.Add(BitwardenCrypto.DecryptString(encrypted, key));
                }
                parsed.Entries.Add(new Entry(id, name, username, uris.ToArray()));
                parsed.Credentials.Add(id, (username, password));
                password = null;
            }
            finally { password?.Dispose(); }
        }
        finally { if (itemKey != null) CryptographicOperations.ZeroMemory(itemKey); }
    }

    static string ActiveUser(JsonElement root) =>
        Text(root, "global_account_activeAccountId") ?? throw new LocalVaultUnsupportedException("No signed-in account.");

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>
    /// Parses the file from a buffer that is wiped afterwards (it also holds the CLI's tokens). The CLI may be
    /// rewriting it during a sync, so a torn read is retried.
    /// </summary>
    static T WithDocument<T>(string path, Func<JsonElement, T> read)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte[] bytes;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > MaxFileBytes) throw new LocalVaultUnsupportedException("The local vault is too large.");
                bytes = GC.AllocateUninitializedArray<byte>((int)file.Length, pinned: true);
                file.ReadExactly(bytes);
            }
            catch (FileNotFoundException) { throw new LocalVaultUnsupportedException("No local vault yet."); }
            catch (DirectoryNotFoundException) { throw new LocalVaultUnsupportedException("No local vault yet."); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(100); continue; }

            try
            {
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                return read(doc.RootElement);
            }
            catch (JsonException) when (attempt < 3) { Thread.Sleep(100); }
            catch (JsonException) { throw new LocalVaultUnsupportedException("The local vault file couldn't be read."); }
            catch (InvalidOperationException) { throw new LocalVaultUnsupportedException("The local vault has an unexpected format."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
