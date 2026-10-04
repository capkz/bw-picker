using System.Text.Json;
using Microsoft.Win32;

namespace BwPicker;

/// <summary>Non-secret preferences in %APPDATA%\BwPicker\settings.json.</summary>
sealed class AppSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool Welcomed { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BwPicker", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? DefaultPath);
            return JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Autostart. Normally a per-user Run entry. When BwPicker runs as administrator from Program Files (so only
/// admins can replace the exe), it uses a scheduled task with highest privileges instead: that starts it
/// elevated at sign-in without a UAC prompt, and is refused from user-writable folders because a
/// replaceable exe started as admin would hand admin rights to anything able to swap it.
/// </summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BwPicker";
    const string TaskName = "BwPicker";

    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running app.");
    static string Command => $"\"{Exe}\"";

    /// <summary>Running as administrator from a folder only administrators can modify.</summary>
    public static bool AdminMode => ProcessInfo.CurrentIsElevated && InProtectedFolder(Exe);

    internal static bool InProtectedFolder(string path)
    {
        string full = Path.GetFullPath(path);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(dir => dir.Length > 0)
            .Any(dir => full.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Enabled
    {
        get => AdminMode ? TaskExists() : RunEntry() != null;
        set
        {
            if (AdminMode)
            {
                if (value) CreateTask(); else DeleteTask();
                SetRunEntry(false); // never both: a non-admin start could win the single-instance race
            }
            else SetRunEntry(value);
        }
    }

    /// <summary>
    /// At startup: in admin mode, retire any per-user Run entry in favour of the task. Otherwise re-point an
    /// existing entry at this exe only if the one it names is gone (the app was moved), so running another
    /// copy, such as a development build, doesn't take over autostart.
    /// </summary>
    public static void Refresh()
    {
        if (AdminMode)
        {
            if (RunEntry() != null) { SetRunEntry(false); if (!TaskExists()) CreateTask(); }
            return;
        }
        if (RunEntry() is string current && current != Command && !File.Exists(current.Trim('"'))) SetRunEntry(true);
    }

    static string? RunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    static void SetRunEntry(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    static bool TaskExists() => Schtasks("/Query", "/TN", TaskName) == 0;

    static void DeleteTask()
    {
        if (TaskExists() && Schtasks("/Delete", "/TN", TaskName, "/F") != 0)
            throw new InvalidOperationException("Couldn't remove the startup task.");
    }

    static void CreateTask()
    {
        if (!AdminMode) throw new InvalidOperationException("Admin startup needs BwPicker running as administrator from Program Files.");
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        // Defaults from schtasks would stop the app after 72 hours and on battery power; override them.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts BwPicker at sign-in with administrator rights so it can type into apps that run as administrator.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <Priority>7</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(Exe)}</Command></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"bwpicker-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            if (Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F") != 0)
                throw new InvalidOperationException("Couldn't create the startup task.");
        }
        finally { File.Delete(file); }
    }

    static int Schtasks(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
