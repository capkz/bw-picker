using System.Text.RegularExpressions;

namespace BwPicker;

/// <summary>A window's rectangle on screen, in physical pixels.</summary>
readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>The window that had focus when the hotkey was pressed. Reading and checking it is platform-specific.</summary>
sealed partial record WindowContext(IntPtr Handle, string ProcessName, string AppName, string Title)
{
    public uint ProcessId { get; init; }
    public long StartedAt { get; init; }
    /// <summary>The target runs as administrator and this app doesn't, so typed input would be discarded.</summary>
    public bool BlocksTyping { get; init; }

    /// <summary>
    /// The desktop couldn't tell which app is in front (Wayland without accessibility info for it), so typing goes to
    /// whatever has focus without the identity and focus checks. The picker says so before you choose.
    /// </summary>
    public bool Unverified { get; init; }

    /// <summary>True if the window belongs to the same process instance as when the hotkey was pressed.</summary>
    public bool HasOriginalIdentity()
    {
        if (Unverified) return true;
        if (ProcessId == 0 || StartedAt == 0 || !WindowStillMatches()) return false;
        return ProcessInfo.Read(ProcessId)?.StartedAt == StartedAt;
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
