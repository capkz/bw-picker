using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BwPicker;

/// <summary>Windows 11 style palette, following the system light/dark setting and accent color.</summary>
sealed record Theme(
    bool Dark,
    Color Background,
    Color Surface,
    Color Hover,
    Color Selected,
    Color Text,
    Color SubtleText,
    Color Divider,
    Color Border,
    Color Accent,
    Color OnAccent,
    Color Critical)
{
    static readonly HashSet<string> Families = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
    static readonly string TextFamily = Pick("Segoe UI Variable Text", "Segoe UI");
    static readonly string SemiboldFamily = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    static readonly string DisplayFamily = Pick("Segoe UI Variable Display", "Segoe UI");
    static readonly string IconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    public const string SearchGlyph = "";
    public const string LockGlyph = "";

    /// <summary>Overrides the system setting; only used by `--preview`.</summary>
    public static bool? ForceDark { get; set; }

    public static Theme Current()
    {
        bool dark = ForceDark ?? ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") == 0;
        Color accent = ReadAccent() ?? Color.FromArgb(0, 120, 212);

        return dark
            ? new Theme(true,
                Background: Rgb(0x202020), Surface: Rgb(0x2C2C2C), Hover: Rgb(0x2A2A2A), Selected: Rgb(0x343434),
                Text: Rgb(0xFFFFFF), SubtleText: Rgb(0xA3A3A3), Divider: Rgb(0x2E2E2E), Border: Rgb(0x404040),
                Accent: Blend(accent, Color.White, 0.4f), OnAccent: Rgb(0x000000), Critical: Rgb(0xFF99A4))
            : new Theme(false,
                Background: Rgb(0xFAFAFA), Surface: Rgb(0xEFEFEF), Hover: Rgb(0xF2F2F2), Selected: Rgb(0xEAEAEA),
                Text: Rgb(0x1B1B1B), SubtleText: Rgb(0x626262), Divider: Rgb(0xE8E8E8), Border: Rgb(0xD2D2D2),
                Accent: accent, OnAccent: Rgb(0xFFFFFF), Critical: Rgb(0xC42B1C));
    }

    public static Font Body(float size) => new(TextFamily, size);
    public static Font Semibold(float size) => SemiboldFamily == TextFamily ? new(TextFamily, size, FontStyle.Bold) : new(SemiboldFamily, size);
    public static Font Display(float size) => new(DisplayFamily, size);
    public static Font Icons(float size) => new(IconFamily, size);

    /// <summary>Rounded corners, system shadow, dark title-bar mode and a subtle border, via DWM.</summary>
    public void ApplyChrome(Form form)
    {
        int dark = Dark ? 1 : 0;
        int round = 2; // DWMWCP_ROUND
        int border = Border.R | Border.G << 8 | Border.B << 16;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Color color, RectangleF r, float radius, float width = 1)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount),
        (int)(a.G + (b.G - a.G) * amount),
        (int)(a.B + (b.B - a.B) * amount));

    static Color Rgb(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);

    static string Pick(string preferred, string fallback) => Families.Contains(preferred) ? preferred : fallback;

    static int? ReadDword(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : null;
    }

    // Stored as 0xAABBGGRR.
    static Color? ReadAccent() =>
        ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor") is int v
            ? Color.FromArgb(v & 0xFF, v >> 8 & 0xFF, v >> 16 & 0xFF)
            : null;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Borderless popup with the system drop shadow and per-monitor DPI helpers.</summary>
class ThemedForm : Form
{
    protected readonly Theme Theme = Theme.Current();

    protected ThemedForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Background;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyChrome(this);
    }

    protected int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    protected static void PrepareGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    protected static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra = 0) =>
        TextRenderer.DrawText(g, text, font, bounds, color,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | extra);
}
