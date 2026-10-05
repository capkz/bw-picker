using System.Security;

namespace BwPicker;

/// <summary>Autostart through a per-user LaunchAgent (~/Library/LaunchAgents), which launchd runs at sign-in.</summary>
static class Startup
{
    const string Label = "com.github.capkz.bwpicker";

    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the running app.");

    static string AgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>macOS has no administrator mode; typing permission comes from the Accessibility setting.</summary>
    public static bool AdminMode => false;

    public static bool Enabled
    {
        get => ProgramPath() != null;
        set
        {
            if (!value) { File.Delete(AgentPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
            string temp = AgentPath + ".tmp";
            File.WriteAllText(temp, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{Label}</string>
                  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Exe)}</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>ProcessType</key><string>Interactive</string>
                </dict>
                </plist>

                """);
            File.Move(temp, AgentPath, overwrite: true);
        }
    }

    /// <summary>Re-points an existing agent at this executable only if the one it names is gone (the app was moved).</summary>
    public static void Refresh()
    {
        if (ProgramPath() is { } current && current != Exe && !File.Exists(current)) Enabled = true;
    }

    static string? ProgramPath()
    {
        if (!File.Exists(AgentPath)) return null;
        string text = File.ReadAllText(AgentPath);
        const string open = "<array><string>";
        int start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        int end = text.IndexOf("</string>", start, StringComparison.Ordinal);
        return end < 0 ? null : System.Net.WebUtility.HtmlDecode(text[start..end]);
    }
}
