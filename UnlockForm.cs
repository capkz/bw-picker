namespace BwPicker;

sealed class UnlockForm : ThemedForm
{
    readonly Font titleFont = Theme.Semibold(14f);
    readonly Font bodyFont = Theme.Body(9.5f);
    readonly Font fieldFont = Theme.Body(11f);
    readonly Font iconFont = Theme.Icons(16f);
    readonly string subtitle;
    readonly TextBox password;
    readonly FlatButton unlock, cancel;
    string message = "";
    bool messageIsError;

    Rectangle FieldBounds => new(S(24), S(118), ClientSize.Width - S(48), S(40));

    public UnlockForm(BwClient bw, BwStatus status)
    {
        Text = "Unlock vault";
        ShowInTaskbar = true; // so it can be found if it ends up behind another window
        ClientSize = new Size(S(400), S(252));

        string server = Uri.TryCreate(status.ServerUrl, UriKind.Absolute, out var u) ? u.Host : "bitwarden.com";
        subtitle = status.Email is { } email ? $"{email} on {server}" : server;

        password = new TextBox
        {
            UseSystemPasswordChar = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = fieldFont,
            PlaceholderText = "Master password",
        };
        var field = FieldBounds;
        password.SetBounds(field.X + S(12), field.Y + (field.Height - password.PreferredHeight) / 2, field.Width - S(24), password.PreferredHeight);

        cancel = new FlatButton(Theme, primary: false) { Text = "Cancel" };
        unlock = new FlatButton(Theme, primary: true) { Text = "Unlock" };
        int buttonWidth = (ClientSize.Width - S(48) - S(8)) / 2;
        cancel.SetBounds(S(24), ClientSize.Height - S(24) - S(34), buttonWidth, S(34));
        unlock.SetBounds(cancel.Right + S(8), cancel.Top, buttonWidth, S(34));
        Controls.AddRange([password, cancel, unlock]);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);

        password.GotFocus += (_, _) => Invalidate(field);
        password.LostFocus += (_, _) => Invalidate(field);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        unlock.Click += async (_, _) => await TryUnlock(bw);
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await TryUnlock(bw); }
        };
        Shown += (_, _) => { Activate(); password.Focus(); };
    }

    async Task TryUnlock(BwClient bw)
    {
        if (password.TextLength == 0 || !unlock.Enabled) return;
        unlock.Enabled = cancel.Enabled = password.Enabled = false;
        SetMessage("Unlocking and syncing…", error: false);
        try
        {
            await bw.Unlock(password.Text);
            password.Clear();
            await bw.Load(sync: true);
            DialogResult = DialogResult.OK;
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message, error: true);
            unlock.Enabled = cancel.Enabled = password.Enabled = true;
            password.SelectAll();
            password.Focus();
        }
    }

    void SetMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
        Invalidate();
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);

        DrawText(g, Theme.LockGlyph, iconFont, new Rectangle(S(24), S(24), S(24), S(28)), Theme.Accent);
        DrawText(g, "Unlock your vault", titleFont, new Rectangle(S(24), S(56), ClientSize.Width - S(48), S(28)), Theme.Text);
        DrawText(g, subtitle, bodyFont, new Rectangle(S(24), S(84), ClientSize.Width - S(48), S(20)), Theme.SubtleText);

        // Fluent text field: filled surface, hairline border, accent underline when focused.
        var field = FieldBounds;
        Theme.FillRounded(g, Theme.Surface, field, S(5));
        Theme.DrawRounded(g, Theme.Border, new RectangleF(field.X + .5f, field.Y + .5f, field.Width - 1, field.Height - 1), S(5));
        if (password.Focused)
            Theme.FillRounded(g, Theme.Accent, new RectangleF(field.X + S(1), field.Bottom - S(2), field.Width - S(2), S(2)), S(1));

        if (message.Length > 0)
            DrawText(g, message, bodyFont, new Rectangle(S(24), field.Bottom + S(8), field.Width, S(22)),
                messageIsError ? Theme.Critical : Theme.SubtleText);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            bodyFont.Dispose();
            fieldFont.Dispose();
            iconFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Flat Fluent button: accent-filled when primary, neutral surface otherwise.</summary>
sealed class FlatButton : Control
{
    readonly Theme theme;
    readonly bool primary;
    bool hovered, pressed;

    public FlatButton(Theme theme, bool primary)
    {
        this.theme = theme;
        this.primary = primary;
        Font = primary ? Theme.Semibold(9.5f) : Theme.Body(9.5f);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

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

        float radius = Height * 0.15f;
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        Theme.FillRounded(g, fill, bounds, radius);
        if (!primary) Theme.DrawRounded(g, theme.Border, bounds, radius);

        Color text = primary ? theme.OnAccent : theme.Text;
        if (!Enabled) text = Theme.Blend(text, theme.Background, 0.5f);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
}
