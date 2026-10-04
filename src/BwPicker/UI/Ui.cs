using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace BwPicker;

enum Notice { Info, Warning, Error }

/// <summary>BwPicker's colours; flat, with dark and light variants that follow the OS setting.</summary>
sealed record Palette(
    bool Dark,
    Color Background, Color Surface, Color Hover, Color Selected,
    Color Text, Color SubtleText, Color Divider, Color Border,
    Color Accent, Color OnAccent, Color Critical)
{
    /// <summary>Overrides the OS setting; only used by `--preview`.</summary>
    public static bool? ForceDark { get; set; }

    public static Palette Current =>
        (ForceDark ?? Application.Current?.ActualThemeVariant == ThemeVariant.Dark) ? DarkPalette : LightPalette;

    static readonly Palette DarkPalette = new(true,
        Background: Color.Parse("#15171E"), Surface: Color.Parse("#20232D"), Hover: Color.Parse("#20232D"),
        Selected: Color.Parse("#292B43"), Text: Color.Parse("#F2F3FA"), SubtleText: Color.Parse("#A1A7BA"),
        Divider: Color.Parse("#292D39"), Border: Color.Parse("#373D4E"), Accent: Color.Parse("#A5A7FF"),
        OnAccent: Color.Parse("#191A33"), Critical: Color.Parse("#FF99A4"));

    static readonly Palette LightPalette = new(false,
        Background: Color.Parse("#FCFCFE"), Surface: Color.Parse("#F1F2F7"), Hover: Color.Parse("#F1F2F7"),
        Selected: Color.Parse("#EEEFFE"), Text: Color.Parse("#202438"), SubtleText: Color.Parse("#687086"),
        Divider: Color.Parse("#E8EAF1"), Border: Color.Parse("#DCDFEA"), Accent: Color.Parse("#6366F1"),
        OnAccent: Color.Parse("#FFFFFF"), Critical: Color.Parse("#C42B1C"));

    public static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
}

/// <summary>Borderless, rounded window in BwPicker's style. Moves by dragging empty space.</summary>
class PanelWindow : Window
{
    protected readonly Palette P = Palette.Current;

    protected PanelWindow()
    {
        Icon = Ui.AppIcon;
        // A thin native border without a title bar keeps Windows 11's rounded corners and shadow.
        WindowDecorations = WindowDecorations.BorderOnly;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(P.Background);
        Foreground = new SolidColorBrush(P.Text);
        FontFamily = Ui.Font;
        // Owner-less windows would otherwise open on the primary monitor; put them where the user is working.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opened += (_, _) =>
        {
            if (!PlacedByCaller)
            {
                PlaceOnMonitor(Native.GetCursorPos(out var cursor) ? new PixelPoint(cursor.X, cursor.Y) : null, 0.25);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PlaceOnMonitor(Native.GetCursorPos(out var c) ? new PixelPoint(c.X, c.Y) : null, 0.25),
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
        RequestedThemeVariant = P.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        PointerPressed += (_, e) =>
        {
            if (e.Source is Panel or Border or TextBlock && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    /// <summary>Set by windows that position themselves (the picker, notifications).</summary>
    protected bool PlacedByCaller { get; set; }

    /// <summary>Centers horizontally on the monitor containing <paramref name="point"/> (physical pixels), at a fraction of its height.</summary>
    protected void PlaceOnMonitor(PixelPoint? point, double fromTop)
    {
        var screen = (point is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        int y = area.Y + (int)(area.Height * fromTop);
        Position = new PixelPoint(area.X + (area.Width - size.Width) / 2,
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height - (int)(16 * screen.Scaling))));
    }

    /// <summary>Shows the window and completes when it closes, with its result. Tray apps have no owner to be modal over.</summary>
    public Task<T?> ShowAndWait<T>(Func<T?> result)
    {
        var done = new TaskCompletionSource<T?>();
        Closed += (_, _) => done.TrySetResult(result());
        Show();
        Activate();
        return done.Task;
    }
}

static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Inter, Cantarell, Noto Sans, sans-serif");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI, Inter, Cantarell, Noto Sans, sans-serif");

    public static WindowIcon AppIcon { get; } = new(typeof(Ui).Assembly.GetManifestResourceStream("BwPicker.AppIcon.ico")!);

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    public static TextBlock Text(string text, double size, Color color, FontWeight weight = FontWeight.Normal, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), FontWeight = weight,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The shield-and-check mark used in window headers.</summary>
    public static Control Shield(Palette p, double size = 32)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 6,0 L 12,2.2 L 10.8,7.8 L 6,12 L 1.2,7.8 L 0,2.2 Z M 3.4,5.8 L 5.4,7.7 L 8.8,4.1"),
            Stroke = Brush(p.Accent), StrokeThickness = 1.1, StrokeJoin = PenLineJoin.Round,
            Width = 12, Height = 12, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), Background = Brush(p.Selected), Child = path };
    }

    /// <summary>Shield tile plus a small caption ("VAULT PICKER") or, with <paramref name="title"/>, a window title.</summary>
    public static Control Header(Palette p, string caption, Control? right = null, bool title = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        grid.Children.Add(Shield(p));
        var label = title ? Text(caption, 16, p.Text, FontWeight.SemiBold) : Text(caption, 12, p.SubtleText);
        label.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (right != null) { Grid.SetColumn(right, 2); grid.Children.Add(right); }
        return grid;
    }

    public static Border Divider(Palette p) => new() { Height = 1, Background = Brush(p.Divider) };

    public static Border Keycap(Palette p, string key) => new()
    {
        Padding = new Thickness(6, 1), MinWidth = 22, CornerRadius = new CornerRadius(4),
        Background = Brush(p.Surface), BorderBrush = Brush(p.Border), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = key, FontSize = 11, Foreground = Brush(p.SubtleText), HorizontalAlignment = HorizontalAlignment.Center },
    };

    public static Button Button(Palette p, string text, bool primary)
    {
        var button = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(8), FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (primary) button.Classes.Add("accent");
        else Neutral(p, button);
        return button;
    }

    /// <summary>Unselected segment in BwPicker's neutral style; the selected one keeps Fluent's accent fill.</summary>
    public static void NeutralToggle(Palette p, ToggleButton toggle)
    {
        var hover = Palette.Blend(p.Surface, p.Dark ? Colors.White : Colors.Black, .06);
        toggle.Resources["ToggleButtonBackground"] = Brush(p.Surface);
        toggle.Resources["ToggleButtonBackgroundPointerOver"] = Brush(hover);
        toggle.Resources["ToggleButtonBackgroundPressed"] = Brush(Palette.Blend(p.Surface, p.Background, .4));
        toggle.Resources["ToggleButtonBorderBrush"] = Brush(p.Border);
        toggle.Resources["ToggleButtonBorderBrushPointerOver"] = Brush(Palette.Blend(p.Border, p.SubtleText, .3));
        toggle.Resources["ToggleButtonForeground"] = Brush(p.Text);
        toggle.Resources["ToggleButtonForegroundPointerOver"] = Brush(p.Text);
        toggle.BorderThickness = new Thickness(1);
    }

    /// <summary>BwPicker's neutral button: surface fill with a hairline border instead of Fluent's grey.</summary>
    public static void Neutral(Palette p, Button button)
    {
        var hover = Palette.Blend(p.Surface, p.Dark ? Colors.White : Colors.Black, .06);
        button.Background = Brush(p.Surface);
        button.BorderBrush = Brush(p.Border);
        button.BorderThickness = new Thickness(1);
        button.Foreground = Brush(p.Text);
        button.Resources["ButtonBackgroundPointerOver"] = Brush(hover);
        button.Resources["ButtonBackgroundPressed"] = Brush(Palette.Blend(p.Surface, p.Background, .4));
        button.Resources["ButtonBorderBrushPointerOver"] = Brush(Palette.Blend(p.Border, p.SubtleText, .3));
        button.Resources["ButtonBorderBrushPressed"] = Brush(p.Border);
        button.Resources["ButtonForegroundPointerOver"] = Brush(p.Text);
        button.Resources["ButtonForegroundPressed"] = Brush(p.Text);
    }

    public static TextBox Field(Palette p, string placeholder, bool secret = false)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder, PasswordChar = secret ? '●' : default, MaxLength = 4096,
            Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(8), FontSize = 14,
            Background = Brush(p.Surface), BorderBrush = Brush(p.Border),
        };
        // Keep the surface colour when hovered/focused (Fluent would switch to black/white); accent border on focus.
        box.Resources["TextControlBackgroundPointerOver"] = Brush(p.Surface);
        box.Resources["TextControlBackgroundFocused"] = Brush(p.Surface);
        box.Resources["TextControlBorderBrushPointerOver"] = Brush(Palette.Blend(p.Border, p.SubtleText, .3));
        box.Resources["TextControlBorderBrushFocused"] = Brush(p.Accent);
        box.Resources["TextControlForegroundFocused"] = Brush(p.Text);
        box.Resources["TextControlForegroundPointerOver"] = Brush(p.Text);
        box.Resources["TextControlPlaceholderForeground"] = Brush(p.SubtleText);
        box.Resources["TextControlPlaceholderForegroundFocused"] = Brush(p.SubtleText);
        box.Resources["TextControlPlaceholderForegroundPointerOver"] = Brush(p.SubtleText);
        box.Resources["TextControlSelectionHighlightColor"] = Brush(Palette.Blend(p.Background, p.Accent, .45));
        return box;
    }

    public static TextBlock Message(Palette p) => new()
    {
        FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 2, 0, 0),
    };

    public static void SetMessage(Palette p, TextBlock block, string text, bool error)
    {
        block.Text = text;
        block.Foreground = Brush(error ? p.Critical : p.SubtleText);
        block.IsVisible = text.Length > 0;
    }

    /// <summary>Accent colours for Fluent controls (focus rings, toggles, primary buttons) in BwPicker's indigo.</summary>
    public static void ApplyAccent(Application app)
    {
        app.Resources["SystemAccentColor"] = Color.Parse("#6366F1");
        app.Resources["SystemAccentColorDark1"] = Color.Parse("#5558E3");
        app.Resources["SystemAccentColorDark2"] = Color.Parse("#4A4CCB");
        app.Resources["SystemAccentColorDark3"] = Color.Parse("#3D3FAE");
        app.Resources["SystemAccentColorLight1"] = Color.Parse("#8B8DF8");
        app.Resources["SystemAccentColorLight2"] = Color.Parse("#A5A7FF");
        app.Resources["SystemAccentColorLight3"] = Color.Parse("#C3C4FF");
    }
}
