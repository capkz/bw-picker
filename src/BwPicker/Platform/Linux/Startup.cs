using System.Text;

namespace BwPicker;

/// <summary>Autostart through an XDG autostart entry (~/.config/autostart/bwpicker.desktop), which desktops run at sign-in.</summary>
static class Startup
{
    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running app.");

    static string EntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "bwpicker.desktop");

    /// <summary>Linux has no administrator mode: X11 delivers typed input to any app.</summary>
    public static bool AdminMode => false;

    public static bool Enabled
    {
        get => ExecPath() != null;
        set
        {
            if (!value) { File.Delete(EntryPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            string temp = EntryPath + ".tmp";
            File.WriteAllText(temp, $"""
                [Desktop Entry]
                Type=Application
                Name=BwPicker
                Comment=Type or copy Bitwarden logins with Ctrl+Alt+B
                Exec={Quote(Exe)}
                Terminal=false
                X-GNOME-Autostart-enabled=true

                """, new UTF8Encoding(false));
            File.Move(temp, EntryPath, overwrite: true);
        }
    }

    /// <summary>Re-points an existing entry at this executable only if the one it names is gone (the app was moved).</summary>
    public static void Refresh()
    {
        if (ExecPath() is { } current && current != Exe && !File.Exists(current)) Enabled = true;
    }

    static string? ExecPath()
    {
        if (!File.Exists(EntryPath)) return null;
        foreach (string line in File.ReadLines(EntryPath))
        {
            if (line.StartsWith("Hidden=true", StringComparison.Ordinal)) return null;
            if (line.StartsWith("Exec=", StringComparison.Ordinal)) return Unquote(line[5..].Trim());
        }
        return null;
    }

    // Desktop entry quoting: wrap in double quotes and escape ", `, $ and \.
    static string Quote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"') return value.Split(' ')[0];
        var path = new StringBuilder();
        for (int i = 1; i < value.Length && value[i] != '"'; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) i++;
            path.Append(value[i]);
        }
        return path.ToString();
    }
}
