using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BwPicker;

/// <summary>The window that had focus when the hotkey was pressed.</summary>
sealed record WindowContext(IntPtr Handle, string ProcessName, string AppName, string Title)
{
    public uint ProcessId { get; init; }
    public long StartedAt { get; init; }

    public bool HasOriginalIdentity()
    {
        if (ProcessId == 0 || StartedAt == 0 || !WindowStillMatches()) return false;
        try { using var process = Process.GetProcessById((int)ProcessId); return process.StartTime.ToUniversalTime().Ticks == StartedAt; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public bool WindowStillMatches()
    {
        if (!Native.IsWindow(Handle)) return false;
        Native.GetWindowThreadProcessId(Handle, out uint pid);
        return pid == ProcessId && ReadTitle(Handle) == Title;
    }

    static string ReadTitle(IntPtr hwnd)
    {
        int length = Native.GetWindowTextLength(hwnd);
        if (length < 0 || length > 8192) throw new InvalidOperationException("Cannot verify the destination window title.");
        var title = new StringBuilder(length + 1);
        Native.GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    public static WindowContext From(IntPtr hwnd)
    {
        string title = ReadTitle(hwnd);
        uint processId = 0;
        long startedAt = 0;

        string process = "", appName = "";
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            processId = pid;
            startedAt = p.StartTime.ToUniversalTime().Ticks;
            process = appName = p.ProcessName;
            // "Discord", "Google Chrome", … Fails for elevated processes, which is fine.
            var description = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) appName = description.Trim();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        return new WindowContext(hwnd, process, appName, title) { ProcessId = processId, StartedAt = startedAt };
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
        string[] hosts = e.Hosts;
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
        return MatchesQuery(e, words);
    }

    public static bool MatchesQuery(Entry e, string[] words) =>
        words.All(w => e.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase));

    // Also handles "apptitle://discord" and bare "discord.com".
    internal static string Host(string uri) =>
        Uri.TryCreate(uri.Contains("://") ? uri : "https://" + uri, UriKind.Absolute, out var u)
            ? u.Host.ToLowerInvariant()
            : uri.ToLowerInvariant();
}
