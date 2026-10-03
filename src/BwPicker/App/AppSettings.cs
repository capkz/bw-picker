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

/// <summary>Per-user autostart through the Run key; follows the exe if it moves or updates.</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BwPicker";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Re-point an existing entry at this exe only if the one it names is gone (the app was moved), so
    /// running another copy, such as a development build, doesn't take over autostart.
    /// </summary>
    public static void Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command && !File.Exists(current.Trim('"')))
            key.SetValue(ValueName, Command);
    }
}
