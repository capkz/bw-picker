using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BwPicker;

/// <summary>A small notification in the corner of the screen, in place of tray balloon tips.</summary>
sealed class Toast : PanelWindow
{
    static Toast? current;

    Toast(string message, Notice kind, Action? onClick)
    {
        Title = "BwPicker";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        PlacedByCaller = true;
        Draggable = false;

        var accent = kind switch { Notice.Error => P.Critical, Notice.Warning => Color.Parse("#E0A43A"), _ => P.Accent };
        var stripe = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Ui.Brush(accent), Margin = new Thickness(0, 2, 12, 2) };
        var text = new StackPanel
        {
            Spacing = 3,
            Children = { Ui.Text("BwPicker", 12, P.SubtleText, FontWeight.SemiBold), Ui.Text(message, 13, P.Text, wrap: true) },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16, 12) };
        grid.Children.Add(stripe);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Content = grid;
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, e) => { e.Handled = true; onClick?.Invoke(); Close(); };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(3 + message.Length / 25.0, 4, 9)) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        Opened += (_, _) =>
        {
            if (Screens.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
                int margin = (int)(16 * screen.Scaling);
                Position = new PixelPoint(area.Right - size.Width - margin, area.Bottom - size.Height - margin);
            }
            timer.Start();
        };
    }

    public static void Show(string message, Notice kind, Action? onClick = null)
    {
        current?.Close();
        current = new Toast(message, kind, onClick);
        current.Closed += (s, _) => { if (ReferenceEquals(current, s)) current = null; };
        current.Show();
    }
}
