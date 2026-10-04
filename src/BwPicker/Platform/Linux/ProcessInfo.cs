namespace BwPicker;

/// <summary>Facts about the process behind a window, from /proc.</summary>
readonly record struct ProcessInfo(long StartedAt, string ImagePath, bool? Elevated)
{
    public static bool CurrentIsElevated { get; } = Environment.IsPrivilegedProcess;

    /// <summary>The short command name (/proc/&lt;pid&gt;/comm), for processes whose executable path isn't readable.</summary>
    public string Command { get; init; } = "";

    public static ProcessInfo? Read(uint processId)
    {
        string folder = $"/proc/{processId}";
        try
        {
            // Field 22 of /proc/<pid>/stat is the start time in clock ticks since boot; it tells a reused pid apart.
            string stat = File.ReadAllText(Path.Combine(folder, "stat"));
            int end = stat.LastIndexOf(')');
            if (end < 0) return null;
            var fields = stat[(end + 2)..].Split(' ');
            if (fields.Length < 20 || !long.TryParse(fields[19], out long started)) return null;

            string image = "";
            try { image = new FileInfo(Path.Combine(folder, "exe")).LinkTarget ?? ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // another user's process

            string command = "";
            try { command = File.ReadAllText(Path.Combine(folder, "comm")).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            return new ProcessInfo(started, image, ReadElevation(folder)) { Command = command };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether the process runs as root (effective uid 0).</summary>
    static bool? ReadElevation(string folder)
    {
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(folder, "status")))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var ids = line[4..].Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
                return ids.Length > 1 ? ids[1] == "0" : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
