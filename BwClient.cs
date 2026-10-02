using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BwPicker;

/// <summary>A vault login without its password; passwords are fetched only when used.</summary>
sealed record Entry(string Id, string Name, string? Username, string[] Uris);

/// <summary>Thin wrapper around the Bitwarden CLI. The session key lives only in memory.</summary>
sealed class BwClient
{
    string? session;

    public bool Unlocked => session != null;
    public IReadOnlyList<Entry> Entries { get; private set; } = [];

    public async Task<string> Status()
    {
        var (_, stdout, _) = await Run(null, "status");
        int start = stdout.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Unexpected `bw status` output.");
        using var doc = JsonDocument.Parse(stdout[start..]);
        return doc.RootElement.GetProperty("status").GetString() ?? "unknown";
    }

    public async Task Unlock(string masterPassword)
    {
        // Passed through the child's environment only, never on the command line or disk.
        var env = new Dictionary<string, string> { ["BWPICKER_PW"] = masterPassword };
        var (code, stdout, stderr) = await Run(env, "unlock", "--passwordenv", "BWPICKER_PW", "--raw");
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(FirstLine(stderr) ?? "Unlock failed.");
        session = stdout.Trim();
    }

    public async Task Load(bool sync)
    {
        if (sync) await Run(null, "sync");

        var (code, stdout, stderr) = await Run(null, "list", "items");
        if (code != 0) throw new InvalidOperationException(FirstLine(stderr) ?? "Could not list vault items.");

        var entries = new List<Entry>();
        using var doc = JsonDocument.Parse(stdout);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetInt32() != 1) continue; // logins only
            if (!item.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.Object) continue;

            string[] uris = login.TryGetProperty("uris", out var u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray()
                    .Select(x => x.TryGetProperty("uri", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .OfType<string>()
                    .ToArray()
                : [];

            entries.Add(new Entry(
                item.GetProperty("id").GetString()!,
                item.GetProperty("name").GetString() ?? "",
                GetString(login, "username"),
                uris));
        }
        Entries = entries;
    }

    public async Task<(string? Username, string? Password)> GetCredentials(string id)
    {
        var (code, stdout, stderr) = await Run(null, "get", "item", id);
        if (code != 0) throw new InvalidOperationException(FirstLine(stderr) ?? "Could not read the item.");
        using var doc = JsonDocument.Parse(stdout);
        var login = doc.RootElement.GetProperty("login");
        return (GetString(login, "username"), GetString(login, "password"));
    }

    public async Task Lock()
    {
        if (session == null) return;
        await Run(null, "lock");
        session = null;
        Entries = [];
    }

    async Task<(int Code, string Stdout, string Stderr)> Run(IDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("bw")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--nointeraction");
        if (session != null) psi.Environment["BW_SESSION"] = session;
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Bitwarden CLI (bw) not found on PATH.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout, await stderr);
        }
    }

    static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
