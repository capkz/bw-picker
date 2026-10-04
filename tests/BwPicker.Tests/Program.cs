using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BwPicker;

namespace BwPicker.Regression;

static class Tests
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Throws(Func<Task> action)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected InvalidOperationException");
    }

    [STAThread] static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Contains("--verify-cli"))
            {
                using var executable = TrustedCli.Open();
                using var live = new BwClient();
                live.Status().GetAwaiter().GetResult();
                Console.WriteLine("PASS: official Bitwarden signature, publisher, file handle and sanitized status execution.");
                return 0;
            }
            ClientChecks().GetAwaiter().GetResult();
            SecurityChecks().GetAwaiter().GetResult();
            AccountChecks().GetAwaiter().GetResult();
            LocalVaultChecks().GetAwaiter().GetResult();
            UpdateChecks();
            LayoutChecks();
            Console.WriteLine("PASS: all regression checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task LocalVaultChecks()
    {
        // Known-answer vectors generated independently with Python's hashlib/cryptography following
        // Bitwarden's scheme: PBKDF2-SHA256 → HKDF-Expand("enc"/"mac") → AES-256-CBC + HMAC-SHA256.
        const string katPassword = "correct horse battery", katSalt = "kat@example.com";
        const string katWrappedUserKey = "2.BwcHBwcHBwcHBwcHBwcHBw==|MbxzEdByl06u1VU7pok6vXWQQ8XqSRii8hLi74l1knyA/MFdKQzJeRRkNyr57cA8wsCRzCAds3BU7QPxNOiZgPGFblZtQad09EPNyII4/T0=|7a6tfEvdtnnIqXrb2ibJlDgmq2IHL8T1nEAsFLJqrO4=";
        const string katName = "2.CQkJCQkJCQkJCQkJCQkJCQ==|H6dmZV8l+DOIhabvPE3uEnm7QjOfPWzrxvIiNQv5nqM=|S57+76BNd7tOo6IWFW01nI5A8+8hENsLD9E2ZmWOM80=";
        const string katStretched = "e291dbaf39c2ca7ead2a9399000d7062f5b2807da9f10b8fb2d0474948bbb3eaaefbd730185271af39bc42254106c0feaecb6911b0c428c85f56c7e98f56c3b8";
        var stretched = BitwardenCrypto.DeriveStretchedMasterKey(katPassword, katSalt, 5000);
        Assert(Convert.ToHexStringLower(stretched) == katStretched, "PBKDF2/HKDF stretched key differs from the independent vector");
        var katUserKey = LocalVault.UnlockUserKey(new LocalVault.Account(katSalt, 5000, katWrappedUserKey), katPassword);
        Assert(katUserKey.AsSpan().SequenceEqual(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()), "User key unwrap differs from the vector");
        Assert(BitwardenCrypto.DecryptString(katName, katUserKey) == "Kät 🔑 vector", "Item decryption differs from the vector");

        bool rejected(Action action)
        {
            try { action(); return false; } catch (System.Security.Cryptography.CryptographicException) { return true; }
        }
        Assert(rejected(() => LocalVault.UnlockUserKey(new LocalVault.Account(katSalt, 5000, katWrappedUserKey), "wrong password")),
            "Wrong master password was not rejected");
        string tamperedMac = katName[..^6] + (katName[^6] == 'A' ? 'B' : 'A') + katName[^5..];
        string tamperedCiphertext = katName.Replace("H6dmZV8l", "H6dmZV8m");
        Assert(rejected(() => BitwardenCrypto.Decrypt(tamperedMac, katUserKey)), "Tampered MAC was accepted");
        Assert(rejected(() => BitwardenCrypto.Decrypt(tamperedCiphertext, katUserKey)), "Tampered ciphertext was accepted");
        Assert(rejected(() => BitwardenCrypto.Decrypt("0." + katName[2..], katUserKey)), "Unauthenticated EncString type accepted");
        Assert(rejected(() => BitwardenCrypto.Decrypt("2.garbage", katUserKey)), "Malformed EncString accepted");

        // A synthetic CLI data.json: a normal login, one with a per-item key, one in trash, one secure note.
        string userId = Guid.NewGuid().ToString();
        var itemKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        string E(string text, byte[] key) => BitwardenCrypto.Encrypt(Encoding.UTF8.GetBytes(text), key);
        object Login(string id, string name, string user, string pass, string uri, byte[] key, string? wrappedKey = null, string? deleted = null) => new
        {
            id, type = 1, name = E(name, key), key = wrappedKey, deletedDate = deleted, organizationId = (string?)null,
            login = new { username = E(user, key), password = E(pass, key), uris = new[] { new { uri = E(uri, key), match = (int?)null } } },
        };
        var ciphers = new Dictionary<string, object>
        {
            ["a"] = Login("a", "Discord", "you@example.com", "pässwörd-1", "https://discord.com", katUserKey),
            ["b"] = Login("b", "Steam", "gamer", "pässwörd-2", "https://store.steampowered.com", itemKey,
                wrappedKey: BitwardenCrypto.Encrypt(itemKey, katUserKey)),
            ["c"] = Login("c", "Old", "x", "y", "https://old.example", katUserKey, deleted: "2026-01-01T00:00:00Z"),
            ["d"] = new { id = "d", type = 2, name = E("A note", katUserKey) },
        };
        object State(object cipherMap, int kdfType = 0) => new Dictionary<string, object?>
        {
            ["global_account_activeAccountId"] = userId,
            ["global_account_accounts"] = new Dictionary<string, object> { [userId] = new { email = katSalt } },
            [$"user_{userId}_masterPasswordUnlock_masterPasswordUnlockKey"] = new
            {
                salt = katSalt, kdf = new { kdfType, iterations = 5000 }, masterKeyWrappedUserKey = katWrappedUserKey,
            },
            [$"user_{userId}_ciphers_ciphers"] = cipherMap,
            [$"user_{userId}_token_refreshToken"] = "secret-refresh-token",
        };
        string vaultPath = Path.Combine(Path.GetTempPath(), $"bwpicker-vault-{Guid.NewGuid():N}.json");
        void WriteState(object content) => File.WriteAllText(vaultPath, System.Text.Json.JsonSerializer.Serialize(content));
        try
        {
            WriteState(State(ciphers));
            using (var vault = LocalVault.ReadVault(vaultPath, katUserKey))
            {
                Assert(vault.Entries.Select(e => e.Name).OrderBy(n => n).SequenceEqual(["Discord", "Steam"]),
                    "Local vault should list exactly the logins not in trash");
                using var steamPassword = vault.Credentials["b"].Password!.Reveal();
                Assert(steamPassword.Characters.SequenceEqual("pässwörd-2"), "Per-item key decryption failed");
                Assert(vault.Entries.Single(e => e.Id == "a").Uris.Single() == "https://discord.com", "URI decryption failed");
            }

            // The client unlocks in-process: no `bw unlock` and no `bw list` processes.
            var calls = new List<string>();
            using (var client = new BwClient((_, args) => { calls.Add(args[0]); return Task.FromResult((1, "", "")); }, vaultPath))
            {
                using var password = new MemorySecret(katPassword.AsSpan());
                var timer = Stopwatch.StartNew();
                await client.Unlock(password);
                await client.Load(sync: false);
                Assert(client.Unlocked && client.Entries.Count == 2, "In-process unlock did not load the vault");
                Assert(!calls.Contains("unlock") && !calls.Contains("list"), "In-process unlock still started the CLI");
                using (var credential = await client.GetCredentials("a"))
                    Assert(credential.Password!.Characters.SequenceEqual("pässwörd-1"), "In-process credentials are wrong");
                Console.WriteLine($"     In-process unlock + load: {timer.ElapsedMilliseconds} ms (5,000 KDF iterations in this test).");
                await client.Lock();
                Assert(!client.Unlocked && client.Entries.Count == 0, "Lock did not clear the in-process vault");
                await Throws(() => client.GetCredentials("a"));
            }

            // Wrong password or unsupported vaults defer to the CLI, which stays the authority.
            calls.Clear();
            using (var client = new BwClient((_, args) => { calls.Add(args[0]); return Task.FromResult((args[0] == "status" ? 0 : 1, """{"status":"locked"}""", "")); }, vaultPath))
            {
                using var wrong = new MemorySecret("not it".AsSpan());
                await Throws(() => client.Unlock(wrong));
                Assert(calls.Contains("unlock") && !client.Unlocked, "Wrong password did not defer to the CLI");
            }
            foreach (var unsupported in new[]
            {
                State(ciphers, kdfType: 1),
                State(new Dictionary<string, object> { ["o"] = new { id = "o", type = 1, organizationId = "org", name = E("Org", katUserKey) } }),
            })
            {
                WriteState(unsupported);
                calls.Clear();
                using var client = new BwClient((_, args) => { calls.Add(args[0]); return Task.FromResult((args[0] == "status" ? 0 : 1, """{"status":"locked"}""", "")); }, vaultPath);
                using var password = new MemorySecret(katPassword.AsSpan());
                await Throws(() => client.Unlock(password));
                Assert(calls.Contains("unlock"), "Argon2/organization vault did not fall back to the CLI");
            }
        }
        finally
        {
            File.Delete(vaultPath);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(stretched);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(katUserKey);
        }
        Console.WriteLine("PASS: in-process unlock matches independent vectors; MAC/tamper/type checks; per-item keys, trash, CLI fallback, lock wipe.");
    }

    static async Task AccountChecks()
    {
        // Official servers map to the CLI's names; self-hosted must be HTTPS without credentials.
        Assert(ServerChoice.FromStatus(null).Kind == ServerKind.BitwardenUs, "Default server is not US cloud");
        Assert(ServerChoice.FromStatus("https://vault.bitwarden.eu").CliValue == ServerChoice.EuUrl, "EU cloud not recognized");
        Assert(new ServerChoice(ServerKind.BitwardenUs).CliValue == "bitwarden.com", "US cloud CLI value wrong");
        Assert(ServerChoice.SelfHosted("vault.example.com/").Url == "https://vault.example.com", "Self-hosted URL not normalized");
        foreach (string bad in new[] { "http://vault.example.com", "https://user:pw@vault.example.com", "", "https://vault.example.com/?x=1" })
            await Throws(() => { ServerChoice.SelfHosted(bad); return Task.CompletedTask; });

        string session = new string('A', 86) + "==";
        var responses = new Queue<(int Code, string Stdout)>();
        var seen = new List<string[]>();
        using var client = new BwClient((_, args) =>
        {
            seen.Add(args);
            if (args[0] == "status") return Task.FromResult((0, """{"status":"unauthenticated"}""", ""));
            var (code, stdout) = responses.Count > 0 ? responses.Dequeue() : (0, "");
            return Task.FromResult((code, stdout, ""));
        });
        using var password = new MemorySecret("master".AsSpan());

        responses.Enqueue((1, """{"success":false,"message":"Login failed. No provider selected."}"""));
        Assert(await client.SignIn("you@example.com", password) == SignInOutcome.NeedsMethod, "Multiple 2FA providers not detected");
        responses.Enqueue((1, """{"success":false,"message":"Code is required."}"""));
        Assert(await client.SignIn("you@example.com", password, TwoStepMethod.Email) == SignInOutcome.NeedsCode, "Missing 2FA code not detected");
        Assert(seen[^1].SequenceEqual(["login", "you@example.com", "--passwordenv", "BWPICKER_PW", "--method", "1", "--response"]),
            "Login arguments are wrong or contain the password");
        responses.Enqueue((1, """{"success":false,"message":"Code is required."}"""));
        await Throws(async () => await client.SignIn("you@example.com", password, TwoStepMethod.Authenticator, "123456")); // new-device check
        responses.Enqueue((1, """{"success":false,"message":"Username or password is incorrect. Try again."}"""));
        await Throws(async () => await client.SignIn("you@example.com", password));
        responses.Enqueue((0, $$$"""{"success":true,"data":{"object":"message","title":"You are logged in!","raw":"{{{session}}}"}}"""));
        Assert(await client.SignIn("you@example.com", password, TwoStepMethod.Authenticator, "123456") == SignInOutcome.SignedIn && client.Unlocked,
            "Successful password sign-in did not unlock");
        await Throws(async () => await client.SignIn("not-an-email", password));

        using var apiClient = new BwClient((_, args) => Task.FromResult(args[0] == "status"
            ? (0, """{"status":"unauthenticated"}""", "") : (0, """{"success":true,"data":{"title":"You are logged in!"}}""", "")));
        using var id = new MemorySecret("user.id".AsSpan());
        using var secret = new MemorySecret("secret".AsSpan());
        Assert(await apiClient.SignInWithApiKey(id, secret) == SignInOutcome.NeedsUnlock && !apiClient.Unlocked,
            "API key sign-in should leave the vault locked");

        using var signedIn = new BwClient((_, args) => Task.FromResult(args[0] == "status"
            ? (0, """{"status":"locked","userEmail":"you@example.com"}""", "") : (0, """{"success":true}""", "")));
        await Throws(() => signedIn.SetServer(new ServerChoice(ServerKind.BitwardenEu))); // must sign out first

        string settingsPath = Path.Combine(Path.GetTempPath(), $"bwpicker-test-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { CheckForUpdates = false, Welcomed = true }.Save(settingsPath);
            var loaded = AppSettings.Load(settingsPath);
            Assert(!loaded.CheckForUpdates && loaded.Welcomed, "Settings did not round-trip");
            File.WriteAllText(settingsPath, "{ not json");
            Assert(AppSettings.Load(settingsPath).CheckForUpdates, "Corrupt settings did not fall back to defaults");
        }
        finally { File.Delete(settingsPath); }
        Console.WriteLine("PASS: server choice, password/2FA/API key sign-in outcomes, sign-out-before-switch, settings storage.");
    }

    static void UpdateChecks()
    {
        static string Release(string tag, bool prerelease = false, string host = "github.com") => $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
             "html_url":"https://github.com/capkz/bw-picker/releases/tag/{{tag}}",
             "assets":[{"name":"BwPicker-win-x64.zip","browser_download_url":"https://{{host}}/capkz/bw-picker/releases/download/{{tag}}/BwPicker-win-x64.zip"},
                       {"name":"SHA256SUMS.txt","browser_download_url":"https://{{host}}/capkz/bw-picker/releases/download/{{tag}}/SHA256SUMS.txt"}]}
            """;
        var current = new Version(1, 2, 0);
        Assert(Updater.ParseRelease(Release("v1.3.0"), current)?.Version == new Version(1, 3, 0), "Newer release not offered");
        Assert(Updater.ParseRelease(Release("v1.2.0"), current) == null, "Same version offered as an update");
        Assert(Updater.ParseRelease(Release("v1.1.9"), current) == null, "Older version offered as an update");
        Assert(Updater.ParseRelease(Release("v2.0.0", prerelease: true), current) == null, "Prerelease offered");
        bool rejected = false;
        try { Updater.ParseRelease(Release("v9.0.0", host: "evil.example.com"), current); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Release asset outside github.com/capkz/bw-picker accepted");

        string hash = new string('a', 64);
        Assert(Updater.ParseChecksum($"{hash}  BwPicker-win-x64.zip\n", "BwPicker-win-x64.zip").Length == 32, "Checksum not parsed");
        Assert(Updater.ParseChecksum($"{new string('b', 64)} *other.zip\n{hash} *BwPicker-win-x64.zip", "BwPicker-win-x64.zip")[0] == 0xAA,
            "Checksum for the wrong file used");
        rejected = false;
        try { Updater.ParseChecksum($"{hash}  other.zip", "BwPicker-win-x64.zip"); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Missing checksum accepted");
        Console.WriteLine("PASS: release version/prerelease/origin checks and checksum parsing.");
    }

    static async Task ClientChecks()
    {
        var calls = new List<string>();
        var syncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string items = """[{"id":"1","name":"Example","type":1,"login":{"username":"user","password":"test-🔑-password","uris":[{"uri":"https://example.com"}]}}]""";
        bool failSync = false;
        var client = new BwClient(async (_, args) =>
        {
            calls.Add(args[0]);
            switch (args[0])
            {
                case "status": await Task.Delay(10); return (0, """{"status":"locked"}""", "");
                case "unlock": return (0, new string('A', 86) + "==", "");
                case "list": return (0, items, "");
                case "sync":
                    if (failSync) return (1, "", "Offline");
                    syncStarted.SetResult(); await releaseSync.Task; return (0, "", "");
                default: return (0, "", "");
            }
        });
        await Task.WhenAll(client.Status(), client.Status());
        Assert(calls.Count == 1, "Status was not shared");
        await Unlock(client);
        await client.Load(sync: false);
        Assert(calls.SequenceEqual(["status", "unlock", "list"]), "Local load waited on sync");
        int before = calls.Count;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            using var credential = await client.GetCredentials("1");
            Assert(credential.Password!.Characters.SequenceEqual("test-🔑-password"), "Protected cache round trip failed");
        }
        Assert(calls.Count == before, "Credential selection spawned CLI commands");
        Console.WriteLine($"PASS: 1,000 protected credential reads: {timer.ElapsedMilliseconds} ms, zero CLI calls.");
        failSync = true;
        await Throws(() => client.Load(sync: true));
        using var retained = await client.GetCredentials("1");
        Assert(client.Entries.Count == 1 && retained.Password != null, "Offline sync lost local vault");
        failSync = false;
        var refresh = client.Load(sync: true);
        await syncStarted.Task;
        using var duringSync = await client.GetCredentials("1");
        Assert(duringSync.Password != null, "Background sync blocked selection");
        var locking = client.Lock();
        Assert(!client.Unlocked && client.Entries.Count == 0, "Lock did not clear immediately");
        Assert(!duringSync.IsValid && !retained.IsValid, "Outstanding credential leases survived revocation");
        await Throws(async () => { await client.GetCredentials("1"); });
        releaseSync.SetResult();
        await Throws(() => refresh);
        await locking;
        Assert(client.Entries.Count == 0, "Late sync restored locked entries");
        var unlockStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalls = new List<string>();
        var cancelled = new BwClient(async (_, args) =>
        {
            cancelCalls.Add(args[0]);
            if (args[0] == "status") return (0, """{"status":"locked"}""", "");
            if (args[0] == "unlock") { unlockStarted.SetResult(); await releaseUnlock.Task; return (0, new string('A', 86) + "==", ""); }
            return (0, "", "");
        });
        var pendingUnlock = Unlock(cancelled);
        await unlockStarted.Task;
        var cancelLock = cancelled.Lock();
        releaseUnlock.SetResult();
        await Throws(() => pendingUnlock);
        await cancelLock;
        Assert(!cancelled.Unlocked && cancelCalls.SequenceEqual(["status", "unlock", "lock"]), "Cancelled unlock retained a session");
        Console.WriteLine("PASS: local-first load, shared status, offline fallback, concurrent sync/lock, protected cache cleared on lock.");
    }

    static async Task Unlock(BwClient client)
    {
        using var secret = new MemorySecret("test".AsSpan());
        await client.Unlock(secret);
    }

    static async Task SecurityChecks()
    {
        using (var secret = new MemorySecret("long-sensitive-fixture-🔑".AsSpan()))
        {
            var encrypted = (byte[])typeof(MemorySecret).GetField("buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(secret)!;
            Assert(!Encoding.UTF8.GetString(encrypted).Contains("sensitive-fixture"), "Retained secret is plaintext");
            var lease = secret.Reveal();
            var plain = (char[])typeof(SecretLease).GetField("characters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
            lease.Dispose(); Assert(plain.All(c => c == 0), "Plaintext lease was not wiped");
            secret.Dispose(); Assert(encrypted.All(b => b == 0), "Encrypted secret was not wiped");
            await Throws(() => { secret.Reveal(); return Task.CompletedTask; });
        }
        var ownership = new ClipboardOwnership();
        ownership.Mark(12); Assert(ownership.Matches(12) && !ownership.Matches(13), "Clipboard ownership mismatch");
        ownership.Forget(); Assert(!ownership.Matches(12), "Clipboard ownership survived clearing");
        var psi = CliEnvironment.Create("C:/verified/bw.exe", ["unlock", "--passwordenv", "BWPICKER_PW", "--raw"]);
        Assert(!psi.UseShellExecute && !psi.Environment.ContainsKey("BW_SESSION") && !psi.Environment.ContainsKey("NODE_OPTIONS") &&
            psi.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] == "1", "CLI environment permits inherited secrets or code injection");
        Assert(!psi.ArgumentList.Any(a => a.Contains("test-password")), "Password appears in command line");
        await Throws(() => { TrustedCli.Verify(Environment.ProcessPath!); return Task.CompletedTask; });
        foreach (string server in new[] { "http://example.com", "https://user:password@example.com", "not-a-url" })
            await Throws(() => { BwClient.ValidateServer(server); return Task.CompletedTask; });
        BwClient.ValidateServer("https://vault.example.com");
        foreach (string invalid in new[] { "[] garbage", "[{", "[{\"id\":\"1\",\"type\":1},{\"id\":\"1\",\"type\":1}]" })
            await Throws(() => { using var vault = VaultParser.Parse(Encoding.UTF8.GetBytes(invalid)); return Task.CompletedTask; });
        const string escaped = """[{"type":1,"id":"x","login":{"password":"a\u0062\n\uD83D\uDD11","username":"u","uris":[{"uri":"https://example.com"}]}}]""";
        using (var vault = VaultParser.Parse(Encoding.UTF8.GetBytes(escaped)))
        using (var password = vault.Credentials["x"].Password!.Reveal())
            Assert(password.Characters.SequenceEqual("ab\n🔑"), "Escaped secret parsing failed");
        await Throws(() => { using var key = BwClient.ParseSession("not-a-key"u8); return Task.CompletedTask; });
        // Real CLI session keys are 64-byte AES-256-CBC-HMAC keys (88 base64 chars); 32-byte keys are not.
        await Throws(() => { using var key = BwClient.ParseSession(Encoding.ASCII.GetBytes(new string('A', 43) + "=")); return Task.CompletedTask; });
        using (BwClient.ParseSession(Encoding.ASCII.GetBytes("Your vault is now unlocked!\n" + new string('A', 86) + "==\n"))) { }
        using (var failedUnlock = new BwClient((_, args) => Task.FromResult(args[0] == "status"
            ? (0, """{"status":"locked"}""", "") : (1, new string('A', 86) + "==", "private-sentinel"))))
        {
            await Throws(() => Unlock(failedUnlock));
            Assert(!failedUnlock.Unlocked, "Failed CLI unlock published a session");
        }
        using (var excessive = new MemoryStream(new byte[100]))
            await Throws(async () => { using var output = await SensitiveBytes.Read(excessive, 10, CancellationToken.None); });
        using var client = new BwClient((_, args) => Task.FromResult(args[0] switch
        {
            "status" => (0, """{"status":"locked"}""", ""),
            "unlock" => (0, new string('A', 86) + "==", ""),
            "list" => (0, """[{"id":"1","type":1,"login":{"username":"user","password":"safe-password"}}]""", ""),
            _ => (0, "", ""),
        }));
        await Unlock(client); await client.Load(false);
        using var credential = await client.GetCredentials("1");
        await Throws(async () => { using var unknown = await client.GetCredentials("unlisted-id"); });
        foreach (string attack in new[] { "foreground", "field", "modifiers", "identity", "blocked" })
        {
            var keyboard = new FakeKeyboard(); bool identity = true;
            keyboard.AfterSend = () =>
            {
                if (keyboard.Sends != 1) return;
                switch (attack)
                {
                    case "foreground": keyboard.Window = new IntPtr(3); break;
                    case "field": keyboard.FieldRoot = new IntPtr(4); break; // focus left for another window
                    case "modifiers": keyboard.Held = true; break;
                    case "identity": identity = false; break;
                    case "blocked": keyboard.Blocked = true; break;
                }
            };
            await Throws(() => { new InputTyper(keyboard).Type(new IntPtr(1), credential, false, () => true, () => identity); return Task.CompletedTask; });
            Assert(keyboard.Sends <= 2, "Typing continued after a destination or input failure");
        }
        // Embedded browsers (e.g. CEF in the Purple launcher) hop focus between their own child windows.
        var hopping = new FakeKeyboard();
        hopping.AfterSend = () => hopping.Field = new IntPtr(10 + hopping.Sends);
        new InputTyper(hopping).Type(new IntPtr(1), credential, false, () => true, () => true);
        Assert(hopping.Sends > 2, "Typing stopped when focus moved between controls inside the same window");
        // Chromium/Electron (e.g. Discord) briefly reports no focus after reactivation, and occasionally mid-typing.
        var settling = new FakeKeyboard { FocusDropouts = 10 };
        settling.AfterSend = () => { if (settling.Sends == 3) settling.FocusDropouts = 3; };
        new InputTyper(settling).Type(new IntPtr(1), credential, false, () => true, () => true);
        Assert(settling.Sends == credential.Username!.Length + 1 + credential.Password!.Characters.Length,
            "Typing stopped on a brief focus dropout");
        // Two-step logins: each page gets only its own field, and nothing else is typed.
        var usernameOnly = new FakeKeyboard();
        new InputTyper(usernameOnly).Type(new IntPtr(1), credential, false, () => true, () => true, TypeFields.UsernameOnly);
        Assert(usernameOnly.Sends == credential.Username!.Length, "Username-only typing sent extra keys");
        var passwordOnly = new FakeKeyboard();
        new InputTyper(passwordOnly).Type(new IntPtr(1), credential, true, () => true, () => true, TypeFields.PasswordOnly);
        Assert(passwordOnly.Sends == credential.Password!.Characters.Length + 1, "Password-only typing sent extra keys");
        var held = new FakeKeyboard { Held = true };
        await Throws(() => { new InputTyper(held).Type(new IntPtr(1), credential, false, () => true, () => true); return Task.CompletedTask; });
        Assert(held.Sends == 0, "Typing ignored held modifiers");
        var revoked = new FakeKeyboard();
        Task? midTypeLock = null;
        revoked.AfterSend = () => { midTypeLock ??= client.Lock(); };
        await Throws(() => { new InputTyper(revoked).Type(new IntPtr(1), credential, true, () => true, () => true); return Task.CompletedTask; });
        Assert(revoked.Sends == 1, "Typing or submitting continued after mid-stream lock");
        if (midTypeLock != null) await midTypeLock;
        using var newline = new SecretLease("bad\npassword"u8);
        using var unsafeCredential = new CredentialLease("user", newline, () => true);
        var rejected = new FakeKeyboard();
        await Throws(() => { new InputTyper(rejected).Type(new IntPtr(1), unsafeCredential, true, () => true, () => true); return Task.CompletedTask; });
        Assert(rejected.Sends == 0, "Control-character credential produced input before validation");
        using var sample = BwClient.Preview([]);
        await Throws(() => Unlock(sample));
        var denied = new BwClient((_, args) => Task.FromResult(args[0] == "status"
            ? (0, """{"status":"locked","serverUrl":"http://example.com"}""", "") : (1, "", "private-sentinel")));
        await Throws(() => Unlock(denied));
        await Task.Run(() => client.Block());
        Assert(!credential.IsValid, "Cross-thread OS lock did not revoke leases");
        await Throws(() => Unlock(client));
        client.AllowInteraction();
        Assert(!client.Unlocked, "Windows unlock silently restored the vault session");
        Console.WriteLine("PASS: secret zeroing, unsigned CLI rejection, sanitized environment, HTTPS policy, malformed JSON, output limits, typing guards, cross-thread revocation.");
    }

    sealed class FakeKeyboard : IKeyboard
    {
        public IntPtr Window = new(1), Field = new(2), FieldRoot = new(1);
        public bool Held, Blocked;
        public int Sends;
        public Action? AfterSend;
        public IntPtr Foreground => Window;
        public bool ModifiersDown => Held;
        public bool Focus(IntPtr window) => true;
        public int FocusDropouts; // checks that report no focus, like Chromium right after activation
        public bool FocusInside(IntPtr window)
        {
            if (FocusDropouts > 0) { FocusDropouts--; return false; }
            return Field != IntPtr.Zero && FieldRoot == window;
        }
        public void Wait(int milliseconds) { }
        public uint Send(Native.INPUT[] inputs) { Sends++; AfterSend?.Invoke(); return Blocked ? 0u : (uint)inputs.Length; }
    }

    static void LayoutChecks()
    {
        Theme.ForceDark = true;
        var client = BwClient.Preview(Enumerable.Range(0, 1000).Select(i =>
            new Entry(i.ToString(), "A long example login " + i, "user@example.com", ["https://example.com"])));
        var target = new WindowContext(IntPtr.Zero, "", "Microsoft Edge", "");
        using var picker = new PickerForm(client, target, (_, _) => { }) { CloseOnDeactivate = false };
        using var unlock = new UnlockForm(new BwClient(), new BwStatus("locked", "you@example.com", "https://vault.example.com"));
        foreach (var form in new Form[] { picker, unlock })
        {
            form.Show(); Application.DoEvents();
            foreach (int dpi in new[] { 96, 120, 144, 192, 96 })
            {
                var rect = new Rect { Left = form.Left, Top = form.Top, Right = form.Right, Bottom = form.Bottom };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
                try { Marshal.StructureToPtr(rect, ptr, false); SendMessage(form.Handle, 0x02E0, new IntPtr(dpi | dpi << 16), ptr); }
                finally { Marshal.FreeHGlobal(ptr); }
                Application.DoEvents();
                Assert(form.DeviceDpi == dpi, "DPI transition did not apply");
                int S(int n) => (int)Math.Round(n * dpi / 96f);
                var textbox = form.Controls.OfType<TextBox>().Single();
                Assert(textbox.Font.Unit == GraphicsUnit.Pixel && Math.Abs(textbox.Font.Size - 11 * dpi / 72f) < .1f, "Text and layout scale diverged");
                var field = form is UnlockForm
                    ? new Rectangle(S(24), S(150), form.Width - S(48), S(40))
                    : new Rectangle(S(20), S(64), form.Width - S(40), S(40));
                Assert(field.Contains(textbox.Bounds), $"Text box escaped field at {dpi} DPI");
                foreach (Control control in form.Controls) Assert(form.ClientRectangle.Contains(control.Bounds), "Control outside window");
                if (form is PickerForm)
                {
                    var results = form.Controls.OfType<ResultList>().Single();
                    Assert(results.RowHeight == S(56) && results.Count == 1000, "Result DPI or count incorrect");
                    using var nameFont = Theme.Semibold(10.5f, dpi);
                    Assert(TextRenderer.MeasureText("Example", nameFont).Height <= S(22), "Login title clips its row");
                    results.MoveSelection(999);
                    Assert(results.SelectedEntry?.Id != "0", "Navigation failed");
                }
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                Directory.CreateDirectory("bin/previews");
                bmp.Save($"bin/previews/{form.GetType().Name}-{dpi}.png");
            }
            form.Hide();
        }
        Console.WriteLine("PASS: both popup layouts at 100%, 125%, 150%, 200%, then back to 100%; font, field and list bounds verified.");
        var perf = Stopwatch.StartNew();
        using var warm = new PickerForm(client, target, (_, _) => { }) { CloseOnDeactivate = false };
        warm.Show(); Application.DoEvents();
        Console.WriteLine($"Warm picker with 1,000 sample entries: {perf.ElapsedMilliseconds} ms to show.");
    }
}
