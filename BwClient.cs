using System.Buffers;
using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BwPicker;

sealed record Entry(string Id, string Name, string? Username, string[] Uris)
{
    public string SearchText { get; } = $"{Name} {Username} {string.Join(' ', Uris)}";
    public string[] Hosts { get; } = Uris.Select(Matcher.Host).ToArray();
    public string Site { get; } = Uris.Where(u => !u.StartsWith("apptitle://", StringComparison.OrdinalIgnoreCase))
        .Select(Matcher.Host).FirstOrDefault() is { } host ? (host.StartsWith("www.") ? host[4..] : host) : "";
}
sealed record BwStatus(string Status, string? Email, string? ServerUrl);
sealed class CliOutput(int code, SensitiveBytes stdout) : IDisposable
{
    public int Code { get; } = code;
    public SensitiveBytes Stdout { get; } = stdout;
    public void Dispose() => Stdout.Dispose();
}

/// <summary>Serial CLI access, immediate thread-safe revocation, and process-bound encrypted credentials.</summary>
sealed class BwClient : IDisposable
{
    readonly object state = new();
    readonly SemaphoreSlim commands = new(1, 1);
    readonly Func<IDictionary<string, string>?, string[], Task<(int Code, string Stdout, string Stderr)>>? commandRunner;
    MemorySecret? session;
    Dictionary<string, (string? Username, MemorySecret? Password)> credentials = [];
    IReadOnlyList<Entry> entries = [];
    CancellationTokenSource operations = new();
    Task cleanup = Task.CompletedTask;
    Task<BwStatus>? statusTask;
    DateTime statusReadAt;
    int generation, unlocking;
    bool blocked, disposed, preview;

    public BwClient(Func<IDictionary<string, string>?, string[], Task<(int Code, string Stdout, string Stderr)>>? commandRunner = null)
        => this.commandRunner = commandRunner;
    public BwStatus? CachedStatus { get; private set; }
    public event EventHandler? EntriesChanged;
    public event EventHandler? Revoked;
    public bool Unlocked { get { lock (state) return !disposed && !blocked && session != null; } }
    public IReadOnlyList<Entry> Entries { get { lock (state) return entries; } }
    public static BwClient Preview(IEnumerable<Entry> entries) => new() { entries = entries.ToList(), preview = true };

    public Task<BwStatus> Status()
    {
        lock (state)
        {
            EnsureAvailable();
            if (statusTask?.IsCompleted == true && (statusTask.IsFaulted || statusTask.IsCanceled ||
                CachedStatus?.Status == "unauthenticated" || DateTime.UtcNow - statusReadAt > TimeSpan.FromSeconds(30))) statusTask = null;
            return statusTask ??= ReadStatus();
        }
    }

    async Task<BwStatus> ReadStatus()
    {
        int started;
        lock (state) { EnsureAvailable(); started = generation; }
        using var output = await Run(null, started, "status");
        if (output.Code != 0) throw new InvalidOperationException("Could not check Bitwarden status.");
        int start = output.Stdout.Memory.Span.IndexOf((byte)'{');
        if (start < 0) throw new InvalidOperationException("Bitwarden returned an invalid status response.");
        try
        {
            using var doc = JsonDocument.Parse(output.Stdout.Memory[start..]);
            var root = doc.RootElement;
            var account = new BwStatus(GetString(root, "status") ?? "unknown", GetString(root, "userEmail"), GetString(root, "serverUrl"));
            ValidateServer(account.ServerUrl);
            lock (state) { EnsureGeneration(started); CachedStatus = account; statusReadAt = DateTime.UtcNow; }
            return account;
        }
        catch (JsonException) { throw new InvalidOperationException("Bitwarden returned an invalid status response."); }
    }

    internal static void ValidateServer(string? server)
    {
        if (string.IsNullOrEmpty(server)) return;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("The vault server must use HTTPS with a valid certificate.");
    }

    public async Task Unlock(MemorySecret masterPassword)
    {
        int started;
        Task pendingCleanup;
        lock (state) { EnsureAvailable(); started = generation; unlocking++; pendingCleanup = cleanup; }
        try
        {
            await pendingCleanup;
            await Status();
            using var output = await Run(masterPassword, started, "unlock", "--passwordenv", "BWPICKER_PW", "--raw");
            if (output.Code != 0) throw new InvalidOperationException("Unlock failed. Check your master password and Bitwarden account.");
            using var next = ParseSession(output.Stdout.Memory.Span);
            lock (state)
            {
                EnsureGeneration(started);
                session?.Dispose();
                using var lease = next.Reveal();
                session = new MemorySecret(lease.Characters);
            }
        }
        finally { lock (state) unlocking--; }
    }

    internal static MemorySecret ParseSession(ReadOnlySpan<byte> output)
    {
        while (output.Length > 0 && output[^1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t') output = output[..^1];
        var key = output[(output.LastIndexOf((byte)'\n') + 1)..];
        // The CLI's session key is a 64-byte AES-256-CBC-HMAC key: 88 base64 characters.
        Span<byte> decoded = stackalloc byte[64];
        try
        {
            if (key.Length != 88 || Base64.DecodeFromUtf8(key, decoded, out int read, out int written) != OperationStatus.Done ||
                read != 88 || written != 64)
                throw new InvalidOperationException("Unlock failed. Check your master password and Bitwarden account.");
            return new MemorySecret(key);
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    public async Task Load(bool sync)
    {
        int started;
        lock (state) { EnsureUnlocked(); started = generation; }
        if (sync)
        {
            using var synced = await Run(null, started, "sync");
            if (synced.Code != 0) throw new InvalidOperationException("Bitwarden sync failed; local logins remain available.");
        }
        using var output = await Run(null, started, "list", "items");
        if (output.Code != 0) throw new InvalidOperationException("Could not load local Bitwarden logins.");
        var parsed = VaultParser.Parse(output.Stdout.Memory.Span);
        try
        {
            lock (state)
            {
                EnsureGeneration(started);
                EnsureUnlocked();
                ClearCredentials();
                credentials = parsed.Credentials;
                entries = parsed.Entries;
                parsed = new([], []);
            }
        }
        finally { parsed.Dispose(); }
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<CredentialLease> GetCredentials(string id)
    {
        lock (state)
        {
            EnsureUnlocked();
            if (!credentials.TryGetValue(id, out var cached))
                throw new InvalidOperationException("This login is no longer in the unlocked vault. Sync and try again.");
            int version = generation;
            return Task.FromResult(new CredentialLease(cached.Username, cached.Password?.Reveal(), () => IsValid(version)));
        }
    }

    bool IsValid(int version)
    {
        lock (state) return !disposed && !blocked && session != null && generation == version;
    }
    public Task Lock() => Revoke(false);
    public Task Block() => Revoke(true);
    public void AllowInteraction() { lock (state) { if (!disposed) blocked = false; } }

    Task Revoke(bool blockAccess)
    {
        CancellationTokenSource previous;
        Task result;
        lock (state)
        {
            if (disposed) return cleanup;
            bool needsCleanup = session != null || unlocking > 0;
            if (blockAccess) blocked = true;
            generation++;
            session?.Dispose(); session = null;
            entries = []; ClearCredentials(); statusTask = null;
            previous = operations; operations = new();
            var prior = cleanup;
            result = cleanup = needsCleanup ? Task.Run(async () =>
            {
                try { await prior; } catch (InvalidOperationException) { }
                using var locked = await Run(null, null, "lock");
                if (locked.Code != 0) throw new InvalidOperationException("Bitwarden CLI lock failed. Local credentials have been cleared.");
            }) : prior;
        }
        previous.Cancel(); previous.Dispose();
        Revoked?.Invoke(this, EventArgs.Empty);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    void EnsureAvailable()
    {
        if (preview) throw new InvalidOperationException("Preview mode cannot access real vault credentials.");
        if (disposed || blocked) throw new InvalidOperationException("Vault access is blocked while Windows is locked or suspended.");
    }
    void EnsureUnlocked() { EnsureAvailable(); if (session == null) throw new InvalidOperationException("Vault is locked."); }
    void EnsureGeneration(int expected)
    {
        EnsureAvailable();
        if (expected != generation) throw new InvalidOperationException("Vault was locked; the operation was cancelled.");
    }
    void ClearCredentials()
    {
        foreach (var credential in credentials.Values) credential.Password?.Dispose();
        credentials.Clear();
    }

    async Task<CliOutput> Run(MemorySecret? masterPassword, int? version, params string[] args)
    {
        await commands.WaitAsync();
        var timer = Stopwatch.StartNew();
        SecretLease? key = null;
        try
        {
            CancellationToken cancellation;
            lock (state)
            {
                if (version is int expected) EnsureGeneration(expected);
                cancellation = version != null || args[0] == "status" ? operations.Token : CancellationToken.None;
                if (version != null && session != null && args[0] is not "status" and not "unlock") key = session.Reveal();
            }
            if (commandRunner != null)
            {
                var result = await commandRunner(null, args);
                return new CliOutput(result.Code, new SensitiveBytes(Encoding.UTF8.GetBytes(result.Stdout)));
            }
            return await RunProcess(masterPassword, key, cancellation, args);
        }
        finally { key?.Dispose(); Trace.WriteLine($"bw {args[0]}: {timer.ElapsedMilliseconds} ms"); commands.Release(); }
    }

    async Task<CliOutput> RunProcess(MemorySecret? password, SecretLease? key, CancellationToken operation, string[] args)
    {
        using var executable = TrustedCli.Open();
        var psi = CliEnvironment.Create(executable.Path, args);
        using var master = password?.Reveal();
        if (key != null) psi.Environment["BW_SESSION"] = new string(key.Characters);
        if (master != null) psi.Environment["BWPICKER_PW"] = new string(master.Characters);
        Process process;
        try { operation.ThrowIfCancellationRequested(); process = Process.Start(psi)!; }
        catch (OperationCanceledException) { throw new InvalidOperationException("Vault was locked; the operation was cancelled."); }
        catch (Win32Exception) { throw new InvalidOperationException("Could not start the verified Bitwarden CLI."); }
        finally
        {
            psi.Environment.Remove("BW_SESSION"); psi.Environment.Remove("BWPICKER_PW");
            key?.Dispose(); master?.Dispose();
        }
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(args[0] == "status" ? 15 : 45));
            var stdout = SensitiveBytes.Read(process.StandardOutput.BaseStream, 64 * 1024 * 1024, timeout.Token);
            var stderr = SensitiveBytes.Read(process.StandardError.BaseStream, 1024 * 1024, timeout.Token);
            bool handedOff = false;
            try
            {
                var exited = process.WaitForExitAsync(timeout.Token);
                var first = await Task.WhenAny(exited, stdout, stderr);
                await first;
                await Task.WhenAll(exited, stdout, stderr);
                handedOff = true;
                return new CliOutput(process.ExitCode, await stdout);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(operation.IsCancellationRequested
                    ? "Vault was locked; the operation was cancelled." : $"Bitwarden {args[0]} timed out. Please try again.");
            }
            catch (IOException) { throw new InvalidOperationException("Could not read the Bitwarden CLI response."); }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                }
                timeout.Cancel();
                if (!handedOff) { try { (await stdout).Dispose(); } catch (Exception) { } }
                try { (await stderr).Dispose(); } catch (Exception) { }
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (state)
        {
            if (disposed) return;
            disposed = true; generation++;
            session?.Dispose(); session = null;
            entries = []; ClearCredentials(); cancellation = operations;
        }
        cancellation.Cancel(); cancellation.Dispose();
    }
    static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
