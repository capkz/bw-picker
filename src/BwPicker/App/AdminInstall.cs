using System.ComponentModel;
using System.Diagnostics;

namespace BwPicker;

/// <summary>
/// Switches BwPicker between normal and administrator mode on Windows. Administrator mode lets it type into apps
/// that run as administrator; it requires the app to live in Program Files (only admins can change it) and to be
/// started elevated, which the scheduled task in <see cref="Startup"/> does at sign-in.
/// </summary>
static class AdminInstall
{
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BwPicker");

    /// <summary>
    /// Relaunches this exe elevated (one UAC prompt) to install into Program Files and start from there.
    /// False if the user declined the prompt; the caller then keeps running and should shut down on true.
    /// </summary>
    public static bool RequestElevation()
    {
        try
        {
            using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                ArgumentList = { "--install-admin", Environment.ProcessId.ToString() },
            });
            return helper != null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; } // ERROR_CANCELLED: UAC declined
    }

    /// <summary>
    /// Runs in the elevated helper: copies the app (exe + native libraries) into Program Files, then starts that copy,
    /// which waits for the old instance to exit and sets up the administrator startup task.
    /// </summary>
    public static void RunHelper(string[] args)
    {
        if (!ProcessInfo.CurrentIsElevated) return;
        int index = Array.IndexOf(args, "--install-admin");
        string previousPid = index >= 0 && index + 1 < args.Length ? args[index + 1] : "0";
        string source = AppContext.BaseDirectory, target = InstallFolder;
        Directory.CreateDirectory(target);
        if (!string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (!Updater.IsAppFile(name)) continue;
                string destination = Path.Combine(target, name);
                // A file in use (e.g. an older admin copy) can be renamed aside; it's removed at the next start.
                if (File.Exists(destination))
                {
                    if (File.Exists(destination + ".old")) File.Delete(destination + ".old");
                    File.Move(destination, destination + ".old");
                }
                File.Copy(file, destination);
            }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(target, "BwPicker.exe"))
        {
            UseShellExecute = false,
            ArgumentList = { "--enable-admin-autostart", "--updated-from", previousPid },
        })?.Dispose();
    }

    /// <summary>
    /// Leaves administrator mode: removes the elevated startup task (keeping normal autostart if it was on) and starts a
    /// non-elevated copy through Explorer, which runs programs with the user's normal rights. The caller then exits.
    /// </summary>
    public static void LeaveAdminMode()
    {
        bool autostart = Startup.Enabled;
        Startup.LeaveAdminMode(autostart);
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false, ArgumentList = { Environment.ProcessPath! },
        })?.Dispose();
    }
}
