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
    /// Runs in the elevated helper: copies BwPicker.exe into Program Files, then starts that copy, which restores its
    /// native libraries from the copies embedded in it (<see cref="NativeLibraries"/>), waits for the old instance to
    /// exit and sets up the administrator startup task. Only the exe is copied: the folder it runs from is often
    /// Downloads, and any other DLL there would otherwise end up loaded with administrator rights at every sign-in.
    /// </summary>
    public static void RunHelper(string[] args)
    {
        if (!ProcessInfo.CurrentIsElevated) return;
        int index = Array.IndexOf(args, "--install-admin");
        string previousPid = index >= 0 && index + 1 < args.Length ? args[index + 1] : "0";
        string source = Environment.ProcessPath!, target = InstallFolder, exe = Path.Combine(target, "BwPicker.exe");
        Directory.CreateDirectory(target);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            // Files in use (an older admin copy) can be renamed aside; they're removed at the next start. Every DLL goes,
            // so only the libraries the new exe carries are in the folder.
            foreach (string file in Directory.GetFiles(target, "*.dll").Append(exe).Where(File.Exists))
            {
                if (File.Exists(file + ".old")) File.Delete(file + ".old");
                File.Move(file, file + ".old");
            }
            File.Copy(source, exe);
        }
        Process.Start(new ProcessStartInfo(exe)
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
