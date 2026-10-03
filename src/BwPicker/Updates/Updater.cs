using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BwPicker;

static class AppVersion
{
    public static string Text { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0-dev";

    /// <summary>Local builds; they never update themselves automatically.</summary>
    public static bool IsDevelopment => Text.Contains('-');

    public static Version Number { get; } = Version.TryParse(Text.Split('-')[0], out var v) ? v : new Version(0, 0, 0);
}

sealed record ReleaseInfo(Version Version, string Tag, Uri Page, Uri Package, Uri Checksums);

/// <summary>Checks GitHub Releases and installs updates in place, verifying the release's SHA-256 checksum.</summary>
sealed class Updater(AppSettings settings, HttpClient? http = null)
{
    internal const string Repository = "capkz/bw-picker";
    internal const string PackageName = "BwPicker-win-x64.zip";
    const string ChecksumsName = "SHA256SUMS.txt";
    const long MaxPackageBytes = 100 * 1024 * 1024;
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    readonly HttpClient http = http ?? CreateClient();
    bool busy;

    public ReleaseInfo? Available { get; private set; }
    public string Status { get; private set; } = AppVersion.IsDevelopment ? "Development build" : "";
    public bool Busy => busy;
    public event EventHandler? Changed;
    /// <summary>Raised with the path of the verified new exe; the app installs it and restarts.</summary>
    public event EventHandler<string>? ReadyToInstall;

    public bool CheckIsDue =>
        settings.CheckForUpdates && !AppVersion.IsDevelopment &&
        (settings.LastUpdateCheck is not { } last || DateTimeOffset.UtcNow - last > CheckInterval);

    public async Task Check(bool manual)
    {
        if (busy) return;
        SetState(true, "Checking for updates…");
        try
        {
            Available = await FetchLatest(CancellationToken.None);
            settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SetState(false, Available != null ? $"Version {Available.Version.ToString(3)} is available"
                : AppVersion.IsDevelopment && manual ? "Development build; no release to compare with" : "You're up to date");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            SetState(false, manual ? "Couldn't check for updates. " + Describe(e) : Status);
        }
    }

    public async Task Install()
    {
        if (busy || Available is not { } release) return;
        SetState(true, $"Downloading {release.Version.ToString(3)}…");
        try
        {
            string exe = await DownloadVerified(release, CancellationToken.None);
            SetState(true, "Restarting…");
            ReadyToInstall?.Invoke(this, exe);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException)
        {
            SetState(false, "Update failed. " + Describe(e));
        }
    }

    async Task<ReleaseInfo?> FetchLatest(CancellationToken cancel)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // nothing published yet
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancel), AppVersion.Number);
    }

    /// <summary>The newest release if it is newer than <paramref name="current"/> and carries both assets.</summary>
    internal static ReleaseInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Build < 0) return null;
        version = new Version(version.Major, version.Minor, version.Build);
        if (version <= current) return null;

        Uri? package = null, checksums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            var url = TrustedDownload(asset.GetProperty("browser_download_url").GetString());
            if (name == PackageName) package = url;
            else if (name == ChecksumsName) checksums = url;
        }
        if (package == null || checksums == null) return null;
        var page = TrustedDownload(root.GetProperty("html_url").GetString());
        return new ReleaseInfo(version, tag, page, package, checksums);
    }

    static Uri TrustedDownload(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release points outside this repository.");
        return uri;
    }

    /// <summary>Finds "&lt;sha256&gt;  &lt;name&gt;" in a sha256sum-style file.</summary>
    internal static byte[] ParseChecksum(string text, string name)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == name && parts[0].Length == 64)
                return Convert.FromHexString(parts[0]);
        }
        throw new InvalidOperationException("The release has no checksum for the download.");
    }

    async Task<string> DownloadVerified(ReleaseInfo release, CancellationToken cancel)
    {
        string checksums = await http.GetStringAsync(release.Checksums, cancel);
        if (checksums.Length > 64 * 1024) throw new InvalidOperationException("The checksum file is unexpectedly large.");
        byte[] expected = ParseChecksum(checksums, PackageName);

        string folder = Path.Combine(Path.GetTempPath(), "BwPicker-update-" + release.Version.ToString(3));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, PackageName);

        using (var response = await http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidOperationException("The download is unexpectedly large.");
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(zip);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes) throw new InvalidOperationException("The download is unexpectedly large.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
        }

        byte[] actual;
        await using (var file = File.OpenRead(zip)) actual = await SHA256.HashDataAsync(file, cancel);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidOperationException("The download doesn't match the release checksum, so it wasn't installed.");

        string exe = Path.Combine(folder, "BwPicker.exe");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.GetEntry("BwPicker.exe") ?? throw new InvalidOperationException("The release package has no BwPicker.exe.");
            if (entry.Length > MaxPackageBytes) throw new InvalidOperationException("The release package is unexpectedly large.");
            entry.ExtractToFile(exe, overwrite: true);
        }
        var info = FileVersionInfo.GetVersionInfo(exe);
        if (info.ProductVersion?.Split('+')[0] != release.Version.ToString(3))
            throw new InvalidOperationException("The downloaded app reports a different version than the release.");
        return exe;
    }

    void SetState(bool working, string status)
    {
        busy = working;
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    static string Describe(Exception e) => e switch
    {
        HttpRequestException or TaskCanceledException => "Check your internet connection.",
        _ => e.Message,
    };

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BwPicker", AppVersion.Text));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Swaps the running exe for the new one and starts it. Windows allows renaming a running exe, so the
    /// current file becomes BwPicker.exe.old (removed by the new instance) and is restored if anything fails.
    /// </summary>
    public static void InstallAndRelaunch(string newExe)
    {
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running app.");
        string old = current + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"BwPicker can't replace itself in {Path.GetDirectoryName(current)}. " +
                "Move it to a folder you can write to, such as %LOCALAPPDATA%\\Programs\\BwPicker, or update manually.");
        }
        try
        {
            File.Copy(newExe, current);
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = false, ArgumentList = { "--updated-from", Environment.ProcessId.ToString() } });
        }
        catch
        {
            try { if (File.Exists(current)) File.Delete(current); File.Move(old, current); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Run by the new instance: wait for the old one to exit, then remove its renamed exe.</summary>
    public static void FinishUpdate(string[] args)
    {
        int index = Array.IndexOf(args, "--updated-from");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid)) return;
        try { using var previous = Process.GetProcessById(pid); previous.WaitForExit(15_000); }
        catch (ArgumentException) { } // already gone
        string old = Environment.ProcessPath + ".old";
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
