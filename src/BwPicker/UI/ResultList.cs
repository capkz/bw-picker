using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace BwPicker;

/// <summary>Result rows: monogram, name, username and site. Keyboard focus stays in the search box.</summary>
sealed class ResultList : UserControl
{
    public const double RowHeight = 56;
    const int MaxRows = 200;

    static readonly Color[] IdentityColors =
        [Color.FromRgb(99, 102, 241), Color.FromRgb(14, 145, 130), Color.FromRgb(190, 107, 38), Color.FromRgb(170, 76, 143), Color.FromRgb(55, 124, 195)];

    readonly Palette p;
    readonly StackPanel rows = new() { Margin = new Thickness(10) };
    readonly ScrollViewer scroller;
    readonly TextBlock empty;
    List<Entry> items = [];
    int selected = -1;

    public event EventHandler? ItemActivated;
    public int Count => items.Count;
    public Entry? SelectedEntry => selected >= 0 && selected < items.Count ? items[selected] : null;

    public string EmptyText { set => empty.Text = value; }

    public ResultList(Palette palette)
    {
        p = palette;
        Focusable = false;
        scroller = new ScrollViewer { Content = rows, Focusable = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        empty = Ui.Text("", 13, p.SubtleText);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 36);
        Content = new Panel { Children = { scroller, empty } };
    }

    public void SetItems(IEnumerable<Entry> entries, string? keepId = null)
    {
        items = entries.Take(MaxRows).ToList();
        selected = items.Count == 0 ? -1 : Math.Max(0, keepId == null ? 0 : items.FindIndex(e => e.Id == keepId));
        rows.Children.Clear();
        for (int i = 0; i < items.Count; i++) rows.Children.Add(BuildRow(items[i], i));
        empty.IsVisible = items.Count == 0;
        scroller.IsVisible = items.Count > 0;
        Highlight();
    }

    public void MoveSelection(int delta)
    {
        if (items.Count == 0) return;
        selected = Math.Clamp(selected + delta, 0, items.Count - 1);
        Highlight();
        rows.Children[selected].BringIntoView();
    }

    Control BuildRow(Entry entry, int index)
    {
        var identity = Identity(entry.Name);
        var monogram = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(12),
            Background = Ui.Brush(Palette.Blend(p.Background, identity, p.Dark ? .22 : .1)),
            Child = new TextBlock
            {
                Text = Monogram(entry.Name), FontSize = 14, FontWeight = FontWeight.SemiBold,
                Foreground = Ui.Brush(Palette.Blend(identity, p.Text, p.Dark ? .5 : .15)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 12, 0) };
        text.Children.Add(Ui.Text(entry.Name, 14, p.Text, FontWeight.SemiBold));
        if (!string.IsNullOrEmpty(entry.Username)) text.Children.Add(Ui.Text(entry.Username, 12, p.SubtleText));
        var site = Ui.Text(entry.Site, 12, p.SubtleText);
        site.MaxWidth = 150;
        var enter = Ui.Text("↵", 14, p.Accent, FontWeight.SemiBold);
        enter.Margin = new Thickness(14, 0, 4, 0);
        enter.Width = 18;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(monogram);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(site, 2); grid.Children.Add(site);
        Grid.SetColumn(enter, 3); grid.Children.Add(enter);

        var row = new Border
        {
            Height = RowHeight - 6, Margin = new Thickness(0, 3), Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = grid, Cursor = new Cursor(StandardCursorType.Hand),
            Tag = enter,
        };
        row.PointerEntered += (_, _) => { if (index != selected) row.Background = Ui.Brush(p.Hover); };
        row.PointerExited += (_, _) => { if (index != selected) row.Background = Brushes.Transparent; };
        // Handle the press here so the window's drag-to-move doesn't capture the pointer and swallow the click.
        row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) e.Handled = true; };
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            selected = index;
            Highlight();
            ItemActivated?.Invoke(this, EventArgs.Empty);
        };
        return row;
    }

    void Highlight()
    {
        for (int i = 0; i < rows.Children.Count; i++)
        {
            var row = (Border)rows.Children[i];
            bool on = i == selected;
            row.Background = on ? Ui.Brush(p.Selected) : Brushes.Transparent;
            row.BorderBrush = on ? Ui.Brush(Palette.Blend(p.Selected, p.Accent, .3)) : Brushes.Transparent;
            ((Control)row.Tag!).Opacity = on ? 1 : 0;
        }
    }

    static Color Identity(string name)
    {
        uint hash = 0;
        foreach (char c in name) hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
        return IdentityColors[hash % (uint)IdentityColors.Length];
    }

    static string Monogram(string name)
    {
        var c = name.FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "•" : char.ToUpperInvariant(c).ToString();
    }
}
