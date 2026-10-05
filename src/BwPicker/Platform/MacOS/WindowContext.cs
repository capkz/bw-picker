using Avalonia;

namespace BwPicker;

/// <summary>
/// On macOS the destination is the frontmost app (its process ID is the handle) and that app's focused window, read
/// through the Accessibility API. Without Accessibility access the title can't be read and typing is refused anyway.
/// </summary>
sealed partial record WindowContext
{
    /// <summary>Screen rectangle of the focused window, in physical pixels.</summary>
    public ScreenRect? Bounds
    {
        get
        {
            if (Handle == IntPtr.Zero || MacNative.FocusedWindowFrame((int)Handle) is not { } frame) return null;
            double scale = Desktop.Scale;
            return new ScreenRect((int)(frame.X * scale), (int)(frame.Y * scale),
                (int)((frame.X + frame.Width) * scale), (int)((frame.Y + frame.Height) * scale));
        }
    }

    public bool WindowStillMatches()
    {
        if (Handle == IntPtr.Zero) return false;
        var (pid, _) = MacNative.FrontmostApplication();
        return pid == (int)Handle && pid == ProcessId && MacNative.FocusedWindowTitle(pid) == Title;
    }

    public static WindowContext FromForeground()
    {
        var (pid, name) = MacNative.FrontmostApplication();
        if (pid <= 0 || pid == Environment.ProcessId) return new WindowContext(IntPtr.Zero, "", "", "");
        var info = ProcessInfo.Read((uint)pid);
        string process = info is { ImagePath.Length: > 0 } known ? Path.GetFileName(known.ImagePath) : name;
        return new WindowContext(pid, process, name.Length > 0 ? name : process, MacNative.FocusedWindowTitle(pid) ?? "")
        {
            ProcessId = info == null ? 0 : (uint)pid,
            StartedAt = info?.StartedAt ?? 0,
        };
    }
}

/// <summary>Desktop facts the UI needs that Avalonia doesn't expose.</summary>
static class Desktop
{
    /// <summary>Points to physical pixels on the main display (2 on Retina screens).</summary>
    public static double Scale => MacNative.MainScreenScale();

    /// <summary>The mouse position in physical screen pixels.</summary>
    public static PixelPoint? CursorPosition()
    {
        var p = MacNative.MouseLocation();
        return new PixelPoint((int)(p.X * Scale), (int)(p.Y * Scale));
    }
}
