using System.Diagnostics;

namespace BwPicker;

/// <summary>Installs the official Bitwarden CLI with winget, only when the user asks to.</summary>
static class CliInstaller
{
    public const string DownloadPage = "https://bitwarden.com/help/cli/#download-and-install";

    /// <summary>winget is an app execution alias; it is missing on some older or trimmed Windows installs.</summary>
    static string? Winget()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    public static bool CanInstall => Winget() != null;

    /// <summary>
    /// Runs `winget install Bitwarden.CLI` from the winget community source and returns once bw.exe exists.
    /// The installed executable is still verified as signed by Bitwarden Inc. before BwPicker runs it.
    /// </summary>
    public static async Task Install()
    {
        string winget = Winget() ?? throw new InvalidOperationException("winget isn't available on this PC.");
        var psi = new ProcessStartInfo(winget)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "install", "--id", "Bitwarden.CLI", "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity",
        }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start winget.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("Installing the Bitwarden CLI took too long. Try again, or install it manually.");
        }
        await Task.WhenAll(output, errors);

        // winget's "already installed" exit code is fine as long as the executable is now in place.
        if (!TrustedCli.IsInstalled)
            throw new InvalidOperationException($"winget couldn't install the Bitwarden CLI (exit code {process.ExitCode}). Install it manually instead.");
    }
}
