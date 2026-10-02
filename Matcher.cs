using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BwPicker;

/// <summary>The window that had focus when the hotkey was pressed.</summary>
sealed record WindowContext(IntPtr Handle, string ProcessName, string AppName, string Title)
{
    public static WindowContext From(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        Native.GetWindowText(hwnd, title, title.Capacity);

        string process = "", appName = "";
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            process = appName = p.ProcessName;
            // "Discord", "Google Chrome", … Fails for elevated processes, which is fine.
            var description = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) appName = description.Trim();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        return new WindowContext(hwnd, process, appName, title.ToString());
    }
}

/// <summary>Ranks vault entries by how well they fit the focused app.</summary>
sealed class Matcher
{
    // Words that say nothing about which account belongs to the window.
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "new", "tab", "window", "home", "login", "sign", "app", "application",
        "google", "chrome", "microsoft", "msedge", "edge", "firefox", "mozilla", "opera", "brave", "vivaldi",
        "windows", "explorer", "applicationframehost", "electron", "java", "javaw", "cmd", "powershell",
    };

    readonly string processToken;
    readonly string[] titleWords;

    public Matcher(WindowContext context)
    {
        string process = context.ProcessName.ToLowerInvariant();
        processToken = process.Length >= 3 && !Generic.Contains(process) ? process : "";
        titleWords = Regex.Split(context.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3 && !Generic.Contains(w) && w != processToken)
            .Distinct()
            .ToArray();
    }

    /// <summary>Search text to start with, e.g. "discord" for Discord.exe, if any entry matches it.</summary>
    public string SuggestedQuery(IEnumerable<Entry> entries) =>
        processToken.Length > 0 && entries.Any(e => MatchesQuery(e, processToken)) ? processToken : "";

    public int Score(Entry e)
    {
        string name = e.Name.ToLowerInvariant();
        string[] hosts = e.Uris.Select(Host).ToArray();
        int score = 0;

        if (processToken.Length > 0)
        {
            if (name.Contains(processToken)) score += 10;
            if (hosts.Any(h => h.Contains(processToken))) score += 8;
        }
        foreach (var w in titleWords)
        {
            if (name.Contains(w)) score += 3;
            if (hosts.Any(h => h.Contains(w))) score += 2;
        }
        return score;
    }

    public static bool MatchesQuery(Entry e, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        string haystack = $"{e.Name} {e.Username} {string.Join(' ', e.Uris)}";
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // Also handles "apptitle://discord" and bare "discord.com".
    static string Host(string uri) =>
        Uri.TryCreate(uri.Contains("://") ? uri : "https://" + uri, UriKind.Absolute, out var u)
            ? u.Host.ToLowerInvariant()
            : uri.ToLowerInvariant();
}
