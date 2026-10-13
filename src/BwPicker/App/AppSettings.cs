using System.Text.Json;

namespace BwPicker;

/// <summary>Non-secret preferences in %APPDATA%\BwPicker\settings.json.</summary>
sealed class AppSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool Welcomed { get; set; }

    /// <summary>Lock the vault after this many idle minutes; 0 never locks on idle (screen lock, sleep and sign-out still do).</summary>
    public int AutoLockMinutes { get; set; } = DefaultAutoLockMinutes;

    internal const int DefaultAutoLockMinutes = 15;

    /// <summary>The idle timeout, or null for never; negative values fall back to the default.</summary>
    internal TimeSpan? AutoLockAfter => AutoLockMinutes switch
    {
        0 => null,
        < 0 => TimeSpan.FromMinutes(DefaultAutoLockMinutes),
        _ => TimeSpan.FromMinutes(AutoLockMinutes),
    };

    /// <summary>Windows: run elevated from Program Files so typing reaches apps that run as administrator. On by default.</summary>
    public bool RunAsAdmin { get; set; } = true;

    /// <summary>Linux/Wayland: the desktop's approval for BwPicker to type (RemoteDesktop portal restore token).</summary>
    public string? RemoteDesktopToken { get; set; }

    /// <summary>Set just before switching administrator mode, so the restarted instance reopens Settings and says so.</summary>
    public bool ReopenSettingsAfterSwitch { get; set; }

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
