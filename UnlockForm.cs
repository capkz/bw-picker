namespace BwPicker;

sealed class UnlockForm : ThemedForm
{
    readonly DpiFont titleFont = new(Theme.Semibold, 17f);
    readonly DpiFont bodyFont = new(Theme.Body, 9.5f);
    readonly DpiFont fieldFont = new(Theme.Body, 11f);
    string subtitle;
    string server;
    readonly TextBox password;
    readonly FlatButton unlock, cancel;
    string message = "";
    bool messageIsError;
    bool unlocking;

    Rectangle FieldBounds => new(S(24), S(150), ClientSize.Width - S(48), S(40));

    public UnlockForm(BwClient bw, BwStatus? status = null)
    {
        Text = "Unlock vault";
        ShowInTaskbar = true; // so it can be found if it ends up behind another window
        ClientSize = new Size(S(360), S(292));

        var initialStatus = status ?? bw.CachedStatus;
        server = Uri.TryCreate(initialStatus?.ServerUrl, UriKind.Absolute, out var u) ? u.Host : "bitwarden.com";
        subtitle = initialStatus?.Email ?? "Your Bitwarden account";

        password = new TextBox
        {
            UseSystemPasswordChar = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = fieldFont,
            PlaceholderText = "Master password",
            AccessibleName = "Master password",
            MaxLength = 4096,
        };
        cancel = new FlatButton(Theme, primary: false) { Text = "Cancel" };
        unlock = new FlatButton(Theme, primary: true) { Text = "Unlock vault" };
        Relayout();
        Controls.AddRange([password, cancel, unlock]);
        bw.Revoked += OnRevoked;
        FormClosed += (_, _) => bw.Revoked -= OnRevoked;

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);

        password.GotFocus += (_, _) => Invalidate(FieldBounds);
        password.LostFocus += (_, _) => Invalidate(FieldBounds);
        // Defer until WinForms has finished scaling the child controls.
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Relayout(); });
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        unlock.Click += async (_, _) => await TryUnlock(bw);
        FormClosed += async (_, _) =>
        {
            if (unlocking && DialogResult != DialogResult.OK)
            {
                try { await bw.Lock(); } catch (InvalidOperationException) { }
            }
        };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter && password.Focused) { e.SuppressKeyPress = true; await TryUnlock(bw); }
        };
        Shown += async (_, _) =>
        {
            Relayout();
            var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(workArea.Left + (workArea.Width - Width) / 2,
                Math.Min(workArea.Top + workArea.Height / 4, Math.Max(workArea.Top, workArea.Bottom - Height)));
            Activate(); password.Focus();
            try
            {
                var account = status ?? await bw.Status();
                if (IsDisposed) return;
                subtitle = account.Email ?? "Your Bitwarden account";
                server = Uri.TryCreate(account.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : "bitwarden.com";
                if (account.Status == "unauthenticated")
                {
                    unlock.Enabled = password.Enabled = false;
                    SetMessage("Run bw login in a terminal to sign in first.", error: true);
                }
                Invalidate();
            }
            catch (InvalidOperationException ex) { if (!IsDisposed) SetMessage(ex.Message, error: true); }
        };
    }

    void Relayout()
    {
        SuspendLayout();
        titleFont.SetDpi(DeviceDpi);
        bodyFont.SetDpi(DeviceDpi);
        fieldFont.SetDpi(DeviceDpi);
        ClientSize = new Size(S(360), S(292));
        password.Font = fieldFont;
        cancel.RestoreFont(DeviceDpi);
        unlock.RestoreFont(DeviceDpi);
        var field = FieldBounds;
        password.SetBounds(field.X + S(14), field.Y + (field.Height - password.PreferredHeight) / 2,
            field.Width - S(28), password.PreferredHeight);
        int buttonWidth = (ClientSize.Width - S(48) - S(10)) / 2;
        cancel.SetBounds(S(24), S(232), buttonWidth, S(36));
        unlock.SetBounds(cancel.Right + S(10), cancel.Top, buttonWidth, S(36));
        ResumeLayout(false);
        Invalidate();
    }

    async Task TryUnlock(BwClient bw)
    {
        if (password.TextLength == 0 || !unlock.Enabled) return;
        unlocking = true;
        unlock.Enabled = cancel.Enabled = password.Enabled = false;
        SetMessage("Unlocking vault…", error: false);
        try
        {
            using var masterPassword = new MemorySecret(password.Text.AsSpan());
            password.Clear();
            await bw.Unlock(masterPassword);
            if (IsDisposed) return;
            password.Clear();
            SetMessage("Loading local logins…", error: false);
            await bw.Load(sync: false);
            if (!bw.Unlocked) throw new InvalidOperationException("Vault was locked. Please try again.");
            DialogResult = DialogResult.OK;
        }
        catch (InvalidOperationException ex)
        {
            if (IsDisposed) return;
            if (bw.Unlocked)
            {
                try { await bw.Lock(); } catch (InvalidOperationException) { }
            }
            if (IsDisposed) return;
            SetMessage(ex.Message, error: true);
            unlock.Enabled = cancel.Enabled = password.Enabled = true;
            password.SelectAll();
            password.Focus();
        }
        finally { unlocking = false; }
    }

    void SetMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
        Invalidate();
        Update();
    }

    void OnRevoked(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnRevoked(sender, e)); return; }
        password.Clear();
        DialogResult = DialogResult.Cancel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);

        Theme.FillRounded(g, Theme.Selected, new Rectangle(S(24), S(20), S(32), S(32)), S(10));
        Theme.DrawShield(g, new Rectangle(S(34), S(27), S(12), S(17)), Theme.Accent);
        DrawText(g, "VAULT PICKER", bodyFont, new Rectangle(S(68), S(22), S(250), S(28)), Theme.SubtleText);
        DrawText(g, "Unlock your vault", titleFont, new Rectangle(S(24), S(64), ClientSize.Width - S(48), S(30)), Theme.Text);
        DrawText(g, subtitle, bodyFont, new Rectangle(S(24), S(98), ClientSize.Width - S(48), S(22)), Theme.SubtleText);
        DrawText(g, "Master password", bodyFont, new Rectangle(S(24), S(126), ClientSize.Width - S(48), S(22)), Theme.SubtleText);

        var field = FieldBounds;
        Theme.FillRounded(g, Theme.Surface, field, S(10));
        Theme.DrawRounded(g, password.Focused ? Theme.Accent : Theme.Border, new RectangleF(field.X + .5f, field.Y + .5f, field.Width - 1, field.Height - 1), S(10));

        if (message.Length > 0)
            TextRenderer.DrawText(g, message, bodyFont, new Rectangle(S(24), field.Bottom + S(6), field.Width, S(32)),
                messageIsError ? Theme.Critical : Theme.SubtleText,
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        DrawText(g, server, bodyFont, new Rectangle(S(24), S(270), field.Width, S(20)), Theme.SubtleText, TextFormatFlags.HorizontalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            password.Clear();
            titleFont.Dispose();
            bodyFont.Dispose();
            fieldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

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
