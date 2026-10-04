using Avalonia;

namespace BwPicker;

sealed partial record WindowContext
{
    /// <summary>Screen rectangle of the window itself.</summary>
    public ScreenRect? Bounds => Handle != IntPtr.Zero && X11.Shared is { } x ? x.Bounds(Handle) : null;

    public bool WindowStillMatches()
    {
        if (X11.Shared is not { } x || !x.Exists(Handle)) return false;
        return x.Pid(Handle) == ProcessId && x.Title(Handle) == Title;
    }

    /// <summary>
    /// The active window on X11. On Wayland the compositor doesn't reveal it, so the context is empty and the picker
    /// copies instead of typing.
    /// </summary>
    public static WindowContext FromForeground()
    {
        if (X11.Shared is not { } x) return new WindowContext(IntPtr.Zero, "", "", "");
        IntPtr window = x.ActiveWindow;
        if (window == IntPtr.Zero) return new WindowContext(IntPtr.Zero, "", "", "");
        string title = x.Title(window);
        uint pid = x.Pid(window) ?? 0;
        var info = pid == 0 ? null : ProcessInfo.Read(pid);
        string process = info is { ImagePath.Length: > 0 } known ? Path.GetFileName(known.ImagePath) : info?.Command ?? "";
        string className = x.ClassName(window);
        return new WindowContext(window, process, FriendlyName(className, process), title)
        {
            ProcessId = info == null ? 0 : pid,
            StartedAt = info?.StartedAt ?? 0,
        };
    }

    /// <summary>"discord" → "Discord"; keeps names that already have capitals, like "Mousepad" or "VSCodium".</summary>
    static string FriendlyName(string className, string process)
    {
        string name = className.Length > 0 ? className : process;
        return name.Length > 0 && name.All(c => !char.IsUpper(c)) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }
}

/// <summary>Desktop facts the UI needs that Avalonia doesn't expose.</summary>
static class Desktop
{
    /// <summary>The mouse position in physical screen pixels.</summary>
    public static PixelPoint? CursorPosition() => X11.Shared?.Pointer() is { } p ? new PixelPoint(p.X, p.Y) : null;
}
