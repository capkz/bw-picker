namespace BwPicker;

/// <summary>Owner-drawn result list: monogram, name, username and site per row.</summary>
sealed class ResultList : Control
{
    readonly Theme theme;
    readonly Font nameFont = Theme.Semibold(10.5f);
    readonly Font detailFont = Theme.Body(9f);
    readonly Font monogramFont = Theme.Semibold(11f);
    List<Entry> items = [];
    int selected = -1, hover = -1, scroll;

    public event EventHandler? ItemActivated;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";
    public int Count => items.Count;
    public Entry? SelectedEntry => selected >= 0 ? items[selected] : null;
    public int RowHeight => S(54);
    public int Inset => S(6);

    public ResultList(Theme theme)
    {
        this.theme = theme;
        SetStyle(ControlStyles.Selectable, false); // keyboard focus stays in the search box
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = theme.Background;
    }

    public void SetItems(IEnumerable<Entry> entries)
    {
        items = entries.ToList();
        selected = items.Count > 0 ? 0 : -1;
        hover = -1;
        scroll = 0;
        Invalidate();
    }

    public void MoveSelection(int delta)
    {
        if (items.Count == 0) return;
        selected = Math.Clamp(selected + delta, 0, items.Count - 1);
        EnsureVisible(selected);
        Invalidate();
    }

    void EnsureVisible(int index)
    {
        int top = Inset + index * RowHeight;
        if (top < scroll + Inset) scroll = top - Inset;
        else if (top + RowHeight > scroll + Height - Inset) scroll = top + RowHeight - Height + Inset;
        ClampScroll();
    }

    void ClampScroll() => scroll = Math.Clamp(scroll, 0, Math.Max(0, ContentHeight - Height));

    int ContentHeight => items.Count * RowHeight + Inset * 2;

    int IndexAt(Point p)
    {
        int i = (p.Y + scroll - Inset) / RowHeight;
        return p.Y + scroll >= Inset && i >= 0 && i < items.Count ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(theme.Background);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, detailFont, ClientRectangle, theme.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return;
        }

        int first = Math.Max(0, (scroll - Inset) / RowHeight);
        int last = Math.Min(items.Count - 1, (scroll + Height) / RowHeight);
        for (int i = first; i <= last; i++) DrawRow(g, i);

        if (ContentHeight > Height) DrawScrollIndicator(g);
    }

    void DrawRow(Graphics g, int i)
    {
        var entry = items[i];
        bool isSelected = i == selected;
        var row = new Rectangle(Inset, Inset + i * RowHeight - scroll, Width - Inset * 2, RowHeight);
        var card = Rectangle.Inflate(row, 0, -S(1));

        if (isSelected)
        {
            Theme.FillRounded(g, theme.Selected, card, S(6));
            // Fluent selection pill
            Theme.FillRounded(g, theme.Accent, new RectangleF(card.X, card.Y + card.Height / 2f - S(9), S(3), S(18)), S(1.5f));
        }
        else if (i == hover)
        {
            Theme.FillRounded(g, theme.Hover, card, S(6));
        }

        var mono = new Rectangle(row.X + S(14), row.Y + (row.Height - S(32)) / 2, S(32), S(32));
        Theme.FillRounded(g, isSelected ? Theme.Blend(theme.Selected, theme.Accent, 0.22f) : theme.Surface, mono, S(7));
        TextRenderer.DrawText(g, Monogram(entry.Name), monogramFont, mono, isSelected ? theme.Text : theme.SubtleText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int siteWidth = S(170);
        int textX = mono.Right + S(12);
        int textWidth = row.Right - textX - siteWidth - S(12);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

        bool hasUser = !string.IsNullOrEmpty(entry.Username);
        var nameBounds = hasUser
            ? new Rectangle(textX, row.Y + S(8), textWidth, S(21))
            : new Rectangle(textX, row.Y, textWidth, row.Height);
        TextRenderer.DrawText(g, entry.Name, nameFont, nameBounds, theme.Text, flags);
        if (hasUser)
            TextRenderer.DrawText(g, entry.Username, detailFont, new Rectangle(textX, row.Y + S(28), textWidth, S(18)), theme.SubtleText, flags);

        TextRenderer.DrawText(g, SiteOf(entry), detailFont, new Rectangle(row.Right - siteWidth - S(14), row.Y, siteWidth, row.Height),
            theme.SubtleText, flags | TextFormatFlags.Right);
    }

    void DrawScrollIndicator(Graphics g)
    {
        float track = Height - Inset * 2;
        float thumb = Math.Max(S(24), track * Height / ContentHeight);
        float y = Inset + (track - thumb) * scroll / (ContentHeight - Height);
        Theme.FillRounded(g, Color.FromArgb(90, theme.SubtleText), new RectangleF(Width - S(5), y, S(2.5f), thumb), S(1.25f));
    }

    static string Monogram(string name)
    {
        var c = name.FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "•" : char.ToUpperInvariant(c).ToString();
    }

    static string SiteOf(Entry entry)
    {
        foreach (var uri in entry.Uris)
        {
            if (uri.StartsWith("apptitle://", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(uri.Contains("://") ? uri : "https://" + uri, UriKind.Absolute, out var u) && u.Host.Length > 0)
                return u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
        }
        return "";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (e.Button != MouseButtons.Left || i < 0) return;
        selected = i;
        Invalidate();
        ItemActivated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll -= e.Delta / 120 * RowHeight;
        ClampScroll();
        hover = IndexAt(PointToClient(Cursor.Position));
        Invalidate();
    }

    int S(float px) => (int)Math.Round(px * DeviceDpi / 96f);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            nameFont.Dispose();
            detailFont.Dispose();
            monogramFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
