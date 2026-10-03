namespace BwPicker;

/// <summary>Signs the Bitwarden CLI in with email + master password (and a two-step code), or a personal API key.</summary>
sealed class SignInForm : ThemedForm
{
    static readonly string[] Methods = ["Authenticator", "Email", "YubiKey"];
    static readonly TwoStepMethod[] MethodValues = [TwoStepMethod.Authenticator, TwoStepMethod.Email, TwoStepMethod.YubiKey];

    readonly BwClient bw;
    readonly string serverName;
    readonly DpiFont titleFont = new(Theme.Semibold, 17f);
    readonly DpiFont bodyFont = new(Theme.Body, 9.5f);
    readonly DpiFont fieldFont = new(Theme.Body, 11f);
    readonly TextField email, password, code, clientId, clientSecret;
    readonly Segmented method;
    readonly LinkLabel switchMode;
    readonly FlatButton submit, cancel;

    bool apiKeyMode, needsCode, needsMethod, working;
    string message = "";
    bool messageIsError;
    readonly List<(string Text, int Y)> labels = [];
    int messageY, subtitleY;

    public SignInForm(BwClient bw, string serverName, string? initialEmail = null)
    {
        this.bw = bw;
        this.serverName = serverName;
        Text = "Sign in to Bitwarden";
        ShowInTaskbar = true;

        email = new TextField(Theme, "you@example.com") { Text = initialEmail ?? "" };
        password = new TextField(Theme, "Master password", secret: true);
        code = new TextField(Theme, "Two-step login code");
        clientId = new TextField(Theme, "user.xxxxxxxx-xxxx-…");
        clientSecret = new TextField(Theme, "client_secret", secret: true);
        method = new Segmented(Theme, Methods) { AccessibleName = "Two-step method" };
        switchMode = new LinkLabel
        {
            Text = "Use an API key instead",
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Theme.Background, AutoSize = true, TabStop = true,
        };
        cancel = new FlatButton(Theme, primary: false) { Text = "Cancel" };
        submit = new FlatButton(Theme, primary: true) { Text = "Sign in" };
        foreach (var c in new Control[] { email, password, code, clientId, clientSecret, method, cancel, submit }) c.BackColor = Theme.Background;
        Controls.AddRange([email, password, method, code, clientId, clientSecret, switchMode, cancel, submit]);

        switchMode.LinkClicked += (_, _) => { apiKeyMode = !apiKeyMode; SetMessage("", false); Relayout(); FocusFirst(); };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        submit.Click += async (_, _) => await Submit();
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter && ActiveControl is TextField or TextBox) { e.SuppressKeyPress = true; await Submit(); }
        };
        FormClosed += (_, _) => { foreach (var f in new[] { password, clientSecret }) f.Box.Clear(); };
        MakeDraggable(this);
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Relayout(); });

        Relayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 4));
        Shown += (_, _) => { Relayout(); Activate(); FocusFirst(); };
    }

    void FocusFirst()
    {
        var first = apiKeyMode ? clientId : email.Text.Length > 0 ? password : email;
        first.Box.Focus();
    }

    void Relayout()
    {
        SuspendLayout();
        titleFont.SetDpi(DeviceDpi); bodyFont.SetDpi(DeviceDpi); fieldFont.SetDpi(DeviceDpi);
        foreach (var f in new[] { email, password, code, clientId, clientSecret }) f.Font = fieldFont;
        method.Font = bodyFont;
        switchMode.Font = bodyFont;
        cancel.RestoreFont(DeviceDpi); submit.RestoreFont(DeviceDpi);

        int pad = S(24), width = S(380), fieldWidth = width - pad * 2;
        labels.Clear();
        subtitleY = S(98);
        int y = S(132);

        void Field(Control control, string label, bool visible, int height = 40)
        {
            control.Visible = visible;
            if (!visible) return;
            labels.Add((label, y));
            control.SetBounds(pad, y + S(22), fieldWidth, S(height));
            y += S(22 + height + 12);
        }

        Field(email, "Email", !apiKeyMode);
        Field(password, "Master password", !apiKeyMode);
        Field(method, "Two-step method", !apiKeyMode && needsMethod, 36);
        Field(code, "Two-step code", !apiKeyMode && (needsCode || needsMethod));
        Field(clientId, "client_id", apiKeyMode);
        Field(clientSecret, "client_secret", apiKeyMode);

        messageY = y;
        int messageHeight = message.Length > 0 || apiKeyMode ? S(apiKeyMode && message.Length == 0 ? 54 : 40) : 0;
        y += messageHeight;
        switchMode.Text = apiKeyMode ? "Use email and password instead" : "Use an API key instead";
        switchMode.Location = new Point(pad, y);
        y += switchMode.PreferredHeight + S(18);

        int buttonWidth = (fieldWidth - S(10)) / 2;
        cancel.SetBounds(pad, y, buttonWidth, S(36));
        submit.SetBounds(cancel.Right + S(10), y, buttonWidth, S(36));
        y += S(36 + 24);

        ClientSize = new Size(width, y);
        ResumeLayout(false);
        Invalidate();
    }

    async Task Submit()
    {
        if (working) return;
        working = true;
        foreach (var c in new Control[] { email, password, code, clientId, clientSecret, method, submit, switchMode }) c.Enabled = false;
        SetMessage(apiKeyMode ? "Signing in…" : "Signing in… this can take a few seconds.", false);
        try
        {
            if (apiKeyMode) await SubmitApiKey();
            else await SubmitPassword();
        }
        catch (InvalidOperationException ex) { if (!IsDisposed) SetMessage(ex.Message, true); }
        finally
        {
            working = false;
            if (!IsDisposed)
                foreach (var c in new Control[] { email, password, code, clientId, clientSecret, method, submit, switchMode }) c.Enabled = true;
        }
    }

    async Task SubmitPassword()
    {
        if (password.Box.TextLength == 0) throw new InvalidOperationException("Enter your master password.");
        using var secret = new MemorySecret(password.Text.AsSpan());
        TwoStepMethod? chosen = needsMethod ? MethodValues[method.SelectedIndex] : null;
        string? token = (needsCode || needsMethod) && code.Text.Trim().Length > 0 ? code.Text.Trim() : null;

        var outcome = await bw.SignIn(email.Text, secret, chosen, token);
        if (IsDisposed) return;
        switch (outcome)
        {
            case SignInOutcome.SignedIn:
                password.Box.Clear();
                SetMessage("Loading logins…", false);
                await bw.Load(sync: false);
                DialogResult = DialogResult.OK;
                break;
            case SignInOutcome.NeedsUnlock:
                password.Box.Clear();
                DialogResult = DialogResult.OK;
                break;
            case SignInOutcome.NeedsMethod:
                needsMethod = true;
                SetMessage("Your account has more than one two-step method. Pick one, then enter its code.", false);
                code.Box.Focus();
                break;
            case SignInOutcome.NeedsCode:
                needsCode = true;
                SetMessage(chosen == TwoStepMethod.Email ? "We've emailed you a code. Enter it to finish signing in."
                    : "Enter the code from your two-step login method.", false);
                code.Box.Focus();
                break;
        }
    }

    async Task SubmitApiKey()
    {
        if (clientId.Text.Trim().Length == 0 || clientSecret.Box.TextLength == 0)
            throw new InvalidOperationException("Enter both the client_id and client_secret.");
        using var id = new MemorySecret(clientId.Text.Trim().AsSpan());
        using var secret = new MemorySecret(clientSecret.Text.Trim().AsSpan());
        await bw.SignInWithApiKey(id, secret);
        if (IsDisposed) return;
        clientSecret.Box.Clear();
        DialogResult = DialogResult.OK; // the caller asks for the master password to unlock
    }

    void SetMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
        Relayout();
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);
        int pad = S(24), fieldWidth = ClientSize.Width - pad * 2;

        Theme.FillRounded(g, Theme.Selected, new Rectangle(pad, S(20), S(32), S(32)), S(10));
        Theme.DrawShield(g, new Rectangle(pad + S(10), S(27), S(12), S(17)), Theme.Accent);
        DrawText(g, "VAULT PICKER", bodyFont, new Rectangle(S(68), S(22), S(250), S(28)), Theme.SubtleText);
        DrawText(g, "Sign in", titleFont, new Rectangle(pad, S(64), fieldWidth, S(30)), Theme.Text);
        DrawText(g, $"to {serverName}", bodyFont, new Rectangle(pad, subtitleY, fieldWidth, S(22)), Theme.SubtleText);

        foreach (var (text, y) in labels)
            DrawText(g, text, bodyFont, new Rectangle(pad, y, fieldWidth, S(20)), Theme.SubtleText);

        string shown = message.Length > 0 ? message : apiKeyMode
            ? "Find it in the web vault: Settings → Security → Keys → View API key. API key sign-in skips the new-device email check."
            : "";
        if (shown.Length > 0)
            TextRenderer.DrawText(g, shown, bodyFont, new Rectangle(pad, messageY, fieldWidth, S(52)),
                messageIsError ? Theme.Critical : Theme.SubtleText, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); fieldFont.Dispose(); }
        base.Dispose(disposing);
    }
}
