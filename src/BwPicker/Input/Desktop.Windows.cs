using Avalonia;

namespace BwPicker;

/// <summary>Desktop facts the UI needs that Avalonia doesn't expose.</summary>
static class Desktop
{
    /// <summary>The mouse position in physical screen pixels.</summary>
    public static PixelPoint? CursorPosition() => Native.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;
}
