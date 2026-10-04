using System.Diagnostics;
using System.Text;

namespace BwPicker;

sealed partial record WindowContext
{
    /// <summary>Screen rectangle of the window itself.</summary>
    public ScreenRect? Bounds => Native.GetWindowRect(Handle, out var r) ? new ScreenRect(r.Left, r.Top, r.Right, r.Bottom) : null;

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

    public static WindowContext FromForeground() => From(Native.GetForegroundWindow());

    static WindowContext From(IntPtr hwnd)
    {
        string title = ReadTitle(hwnd);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var info = pid == 0 ? null : ProcessInfo.Read(pid);
        string process = "", appName = "";
        if (info is { ImagePath.Length: > 0 } known)
        {
            process = appName = Path.GetFileNameWithoutExtension(known.ImagePath);
            try
            {
                // "Discord", "Google Chrome", …
                var description = FileVersionInfo.GetVersionInfo(known.ImagePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) appName = description.Trim();
            }
            catch (FileNotFoundException) { }
        }
        return new WindowContext(hwnd, process, appName, title)
        {
            ProcessId = info == null ? 0 : pid,
            StartedAt = info?.StartedAt ?? 0,
            // Windows (UIPI) drops keystrokes from a non-admin app into an admin one.
            BlocksTyping = !ProcessInfo.CurrentIsElevated && info?.Elevated != false && info != null,
        };
    }
}
