namespace BwPicker;

/// <summary>Flat Fluent button: accent-filled when primary, neutral surface otherwise.</summary>
sealed class FlatButton : Control
{
    readonly Theme theme;
    readonly bool primary;
    readonly DpiFont buttonFont;
    bool hovered, pressed;

    public FlatButton(Theme theme, bool primary)
    {
        this.theme = theme;
        this.primary = primary;
        buttonFont = primary ? new(Theme.Semibold, 9.5f) : new(Theme.Body, 9.5f);
        RestoreFont(DeviceDpi);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public void RestoreFont(int dpi) { buttonFont.SetDpi(dpi); Font = buttonFont; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(theme.Background);

        Color fill = primary ? theme.Accent : theme.Surface;
        if (!Enabled) fill = Theme.Blend(fill, theme.Background, 0.45f);
        else if (pressed) fill = Theme.Blend(fill, theme.Background, 0.25f);
        else if (hovered) fill = Theme.Blend(fill, theme.Dark ? Color.White : Color.Black, 0.06f);

        float radius = Height * 0.23f;
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        Theme.FillRounded(g, fill, bounds, radius);
        if (!primary) Theme.DrawRounded(g, theme.Border, bounds, radius);
        if (Focused) Theme.DrawRounded(g, theme.Accent, new RectangleF(2.5f, 2.5f, Width - 5, Height - 5), radius);

        Color text = primary ? theme.OnAccent : theme.Text;
        if (!Enabled) text = Theme.Blend(text, theme.Background, 0.5f);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) buttonFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Fluent toggle switch.</summary>
sealed class ToggleSwitch : Control
{
    readonly Theme theme;
    bool on, hovered;

    public ToggleSwitch(Theme theme)
    {
        this.theme = theme;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => on;
        set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Shows a value without raising CheckedChanged.</summary>
    public void SetSilently(bool value) { on = value; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(BackColor);

        var track = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
        float r = track.Height / 2;
        Color fill = on ? theme.Accent : theme.Surface;
        if (!Enabled) fill = Theme.Blend(fill, theme.Background, 0.5f);
        else if (hovered) fill = Theme.Blend(fill, theme.Dark ? Color.White : Color.Black, 0.06f);
        Theme.FillRounded(g, fill, track, r);
        if (!on) Theme.DrawRounded(g, theme.SubtleText, track, r, Math.Max(1f, Height / 20f));
        if (Focused) Theme.DrawRounded(g, theme.Accent, RectangleF.Inflate(track, 1, 1), r + 1);

        float knob = track.Height * 0.62f;
        float inset = (track.Height - knob) / 2;
        float x = on ? track.Right - knob - inset : track.X + inset;
        using var brush = new SolidBrush(on ? theme.OnAccent : theme.SubtleText);
        g.FillEllipse(brush, x, track.Y + inset, knob, knob);
    }

    protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = !Checked; } base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { e.SuppressKeyPress = true; Checked = !Checked; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Row of mutually exclusive options, like the Windows 11 segmented control.</summary>
sealed class Segmented : Control
{
    readonly Theme theme;
    readonly string[] options;
    int selected, hover = -1;

    public Segmented(Theme theme, params string[] options)
    {
        this.theme = theme;
        this.options = options;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public event EventHandler? SelectedIndexChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selected;
        set
        {
            value = Math.Clamp(value, 0, options.Length - 1);
            if (selected == value) return;
            selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSilently(int index) { selected = Math.Clamp(index, 0, options.Length - 1); Invalidate(); }

    RectangleF Segment(int i)
    {
        float w = (Width - 6f) / options.Length;
        return new RectangleF(3 + i * w, 3, w, Height - 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(BackColor);

        var outer = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        float radius = Height * 0.23f;
        Theme.FillRounded(g, theme.Surface, outer, radius);
        Theme.DrawRounded(g, Focused ? theme.Accent : theme.Border, outer, radius);

        for (int i = 0; i < options.Length; i++)
        {
            var segment = Segment(i);
            if (i == selected)
            {
                Theme.FillRounded(g, theme.Background, segment, radius - 2);
                Theme.DrawRounded(g, theme.Border, segment, radius - 2);
            }
            else if (i == hover && Enabled)
            {
                Theme.FillRounded(g, Theme.Blend(theme.Surface, theme.Dark ? Color.White : Color.Black, 0.05f), segment, radius - 2);
            }
            Color text = i == selected ? theme.Text : theme.SubtleText;
            if (!Enabled) text = Theme.Blend(text, theme.Background, 0.5f);
            TextRenderer.DrawText(g, options[i], Font, Rectangle.Round(segment), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    int IndexAt(Point p)
    {
        for (int i = 0; i < options.Length; i++)
            if (Segment(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (Enabled && i >= 0) { Focus(); SelectedIndex = i; }
        base.OnMouseClick(e);
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SelectedIndex--;
        else if (e.KeyCode == Keys.Right) SelectedIndex++;
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Rounded text field around a borderless TextBox; the border turns accent while focused.</summary>
sealed class TextField : Control
{
    readonly Theme theme;
    public TextBox Box { get; }

    public TextField(Theme theme, string placeholder, bool secret = false)
    {
        this.theme = theme;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = theme.Surface,
            ForeColor = theme.Text,
            PlaceholderText = placeholder,
            AccessibleName = placeholder,
            UseSystemPasswordChar = secret,
            MaxLength = 4096,
        };
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value; }

    protected override void OnFontChanged(EventArgs e) { Box.Font = Font; base.OnFontChanged(e); LayoutBox(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }
    protected override void OnEnabledChanged(EventArgs e) { Box.Enabled = Enabled; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    void LayoutBox()
    {
        int pad = (int)Math.Round(12 * DeviceDpi / 96f);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Math.Max(0, Width - pad * 2), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(BackColor);
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        float radius = Height * 0.25f;
        Theme.FillRounded(g, theme.Surface, bounds, radius);
        Theme.DrawRounded(g, Box.Focused ? theme.Accent : theme.Border, bounds, radius);
    }
}
