using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace BwPicker;

/// <summary>
/// Installs the official Bitwarden CLI from Bitwarden's GitHub releases, only when the user asks to: downloads the
/// zip for this CPU, checks it against the SHA-256 digest GitHub records for the release asset, and puts bw in
/// BwPicker's own folder (~/.local/share/BwPicker/cli on Linux, ~/Library/Application Support/BwPicker/cli on macOS),
/// where only this user can change it.
/// </summary>
static class CliInstaller
{
    public const string DownloadPage = "https://bitwarden.com/help/cli/#download-and-install";
    const string Releases = "https://api.github.com/repos/bitwarden/clients/releases?per_page=40";
    const long MaxBytes = 200 * 1024 * 1024;

    public static bool CanInstall => RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>How the install happens, for the setup window.</summary>
    public const string Method = "from Bitwarden's GitHub releases";

    public const string Unavailable = "BwPicker can't install the CLI on this computer, so install it from Bitwarden's site, then try again.";

    public static async Task Install()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BwPicker", AppVersion.Text));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        string os = OperatingSystem.IsMacOS() ? "macos" : "linux";
        string prefix = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? $"bw-{os}-arm64-" : $"bw-{os}-";
        (Uri url, byte[] digest) = await FindAsset(http, prefix);

        byte[] zip;
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("The Bitwarden CLI download is unexpectedly large.");
            zip = await response.Content.ReadAsByteArrayAsync();
        }
        if (zip.Length > MaxBytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(zip), digest))
            throw new InvalidOperationException("The Bitwarden CLI download didn't match its published checksum, so it wasn't installed.");

        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = archive.GetEntry("bw") ?? throw new InvalidOperationException("The Bitwarden CLI download has no bw executable.");
        string target = TrustedCli.ManagedPath, folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temp = target + ".new";
        entry.ExtractToFile(temp, overwrite: true);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>The newest stable CLI release's zip for this CPU, with the digest GitHub recorded for it.</summary>
    static async Task<(Uri Url, byte[] Digest)> FindAsset(HttpClient http, string prefix)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(Releases));
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("cli-v", StringComparison.Ordinal)) continue;
            string name = $"{prefix}{tag[5..]}.zip";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
                    throw new InvalidOperationException("The Bitwarden CLI release has no checksum to verify against.");
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://github.com/bitwarden/clients/releases/download/", StringComparison.Ordinal))
                    throw new InvalidOperationException("The Bitwarden CLI release points somewhere unexpected.");
                return (new Uri(url), Convert.FromHexString(digest[7..]));
            }
            break; // the newest CLI release lacks this build; don't fall back to an older one silently
        }
        throw new InvalidOperationException("Couldn't find the Bitwarden CLI for this computer. Install it manually instead.");
    }
}
