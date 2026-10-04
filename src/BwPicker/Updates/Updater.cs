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

/// <summary>
/// A downloaded update: the folder holding its files, and each file's SHA-256 taken from the verified package itself
/// (not from disk), so the copies can be checked again once they are in the app folder.
/// </summary>
sealed record UpdatePayload(string Folder, IReadOnlyDictionary<string, byte[]> Hashes);

/// <summary>Checks GitHub Releases and installs updates in place, verifying the release's SHA-256 checksum.</summary>
sealed class Updater(AppSettings settings, HttpClient? http = null)
{
    internal const string Repository = "capkz/bw-picker";
    /// <summary>This platform's release package, e.g. BwPicker-win-x64.zip or BwPicker-linux-x64.zip.</summary>
    internal static readonly string PackageName = OperatingSystem.IsWindows() ? "BwPicker-win-x64.zip"
        : $"BwPicker-linux-{(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64")}.zip";
    internal static readonly string ExeName = OperatingSystem.IsWindows() ? "BwPicker.exe" : "BwPicker";
    const string ChecksumsName = "SHA256SUMS.txt";
    const long MaxPackageBytes = 300 * 1024 * 1024; // self-contained single-file build
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    readonly HttpClient http = http ?? CreateClient();
    bool busy;

    public ReleaseInfo? Available { get; private set; }
    public string Status { get; private set; } = AppVersion.IsDevelopment ? "Development build" : "";
    public bool Busy => busy;
    public event EventHandler? Changed;
    /// <summary>Raised with the verified new app files; the app installs them and restarts.</summary>
    public event EventHandler<UpdatePayload>? ReadyToInstall;

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
            var payload = await DownloadVerified(release, CancellationToken.None);
            SetState(true, "Restarting…");
            ReadyToInstall?.Invoke(this, payload);
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

    async Task<UpdatePayload> DownloadVerified(ReleaseInfo release, CancellationToken cancel)
    {
        string checksums = await http.GetStringAsync(release.Checksums, cancel);
        if (checksums.Length > 64 * 1024) throw new InvalidOperationException("The checksum file is unexpectedly large.");
        byte[] expected = ParseChecksum(checksums, PackageName);

        // A new folder with a random name, owner-only on Linux (where the temp folder is shared by every user).
        string folder = Directory.CreateTempSubdirectory("BwPicker-update-").FullName;
        string zip = Path.Combine(folder, PackageName);

        // One handle from download to extraction, shared with nobody: the bytes that are hashed are the bytes unpacked.
        await using var file = new FileStream(zip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var response = await http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidOperationException("The download is unexpectedly large.");
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes) throw new InvalidOperationException("The download is unexpectedly large.");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                throw new InvalidOperationException("The download doesn't match the release checksum, so it wasn't installed.");
        }
        await file.FlushAsync(cancel);
        file.Position = 0;

        // The app is the executable plus the native libraries next to it; take only those, from the zip's root.
        string payload = Path.Combine(folder, "app");
        Directory.CreateDirectory(payload);
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long extracted = 0;
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                if (!IsAppFile(entry.FullName)) continue;
                extracted += entry.Length;
                if (entry.Length > MaxPackageBytes || extracted > MaxPackageBytes) throw new InvalidOperationException("The release package is unexpectedly large.");
                hashes[entry.Name] = await Extract(entry, Path.Combine(payload, entry.Name), cancel);
            }
        }
        if (!hashes.ContainsKey(ExeName)) throw new InvalidOperationException($"The release package has no {ExeName}.");
        if (await ReadVersion(Path.Combine(payload, ExeName), cancel) != release.Version.ToString(3))
            throw new InvalidOperationException("The downloaded app reports a different version than the release.");
        return new UpdatePayload(payload, hashes);
    }

    /// <summary>Writes a zip entry to <paramref name="path"/> and returns the SHA-256 of what the package holds.</summary>
    static async Task<byte[]> Extract(ZipArchiveEntry entry, string path, CancellationToken cancel)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>The version a downloaded (and checksum-verified) executable reports.</summary>
    static async Task<string?> ReadVersion(string exe, CancellationToken cancel)
    {
        if (OperatingSystem.IsWindows()) return FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        // ELF files carry no version resource; the new build prints its version and exits before loading any UI.
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--version" },
        }) ?? throw new InvalidOperationException("Couldn't check the downloaded app.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("The downloaded app didn't respond.");
        }
    }

    /// <summary>
    /// The executable or a native library at the zip's root (no folders, so nothing can be written outside the app
    /// folder): .exe/.dll on Windows, BwPicker and *.so on Linux.
    /// </summary>
    internal static bool IsAppFile(string entryName) =>
        entryName.Length > 0 && entryName.IndexOfAny(['/', '\\', ':']) < 0 && !entryName.StartsWith('.') &&
        (OperatingSystem.IsWindows()
            ? entryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            : entryName == ExeName || entryName.EndsWith(".so", StringComparison.Ordinal));

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
    /// Swaps the app's files (exe and native libraries) for the new set and starts the new exe. Windows allows renaming
    /// files that are in use, so each current file becomes "*.old" (removed by the new instance); if anything fails,
    /// every file is put back. Each copy is hashed again once it is in the app folder: the download folder can be
    /// changed by other programs running as this user, while in administrator mode the app folder can't.
    /// </summary>
    public static void InstallAndRelaunch(UpdatePayload payload)
    {
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running app.");
        string folder = Path.GetDirectoryName(current)!;
        var files = payload.Hashes.Keys.Where(IsAppFile).ToList();
        if (!files.Contains(ExeName, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"The update has no {ExeName}.");

        var moved = new List<string>();
        var copied = new List<string>();
        try
        {
            foreach (string name in files)
            {
                string target = Path.Combine(folder, name), old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(target)) { File.Move(target, old); moved.Add(target); }
                File.Copy(Path.Combine(payload.Folder, name), target);
                copied.Add(target);
                if (!CryptographicOperations.FixedTimeEquals(HashFile(target), payload.Hashes[name]))
                    throw new InvalidOperationException("The update's files changed after they were verified, so it wasn't installed.");
            }
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = false, ArgumentList = { "--updated-from", Environment.ProcessId.ToString() } });
        }
        catch (Exception e)
        {
            foreach (string target in copied) { try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            foreach (string target in moved) { try { File.Move(target + ".old", target, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (e is UnauthorizedAccessException)
                throw new InvalidOperationException($"BwPicker can't replace itself in {folder}. Move it to a folder you can write to, such as " +
                    (OperatingSystem.IsWindows() ? "%LOCALAPPDATA%\\Programs\\BwPicker" : "~/.local/share/BwPicker") + ", or update manually.");
            throw;
        }
    }

    static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    /// <summary>
    /// Run at every start. After an update, waits for the previous instance to exit and removes its renamed
    /// exe; always removes leftover downloads (the zip and unpacked exe, ~90 MB per update) from %TEMP%.
    /// </summary>
    public static void FinishUpdate(string[] args)
    {
        int index = Array.IndexOf(args, "--updated-from");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int pid))
        {
            try { using var previous = Process.GetProcessById(pid); previous.WaitForExit(15_000); }
            catch (ArgumentException) { } // already gone
        }
        if (Path.GetDirectoryName(Environment.ProcessPath) is { } appFolder)
        {
            foreach (string old in Directory.EnumerateFiles(appFolder, "*.old"))
            {
                for (int i = 0; i < 20 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
                }
            }
        }
        foreach (string folder in Directory.EnumerateDirectories(Path.GetTempPath(), "BwPicker-update-*"))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // retried next start
        }
    }
}
