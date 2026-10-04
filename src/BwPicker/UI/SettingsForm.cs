namespace BwPicker;

/// <summary>Startup, server/account and update settings.</summary>
sealed class SettingsForm : ThemedForm
{
    const int Width0 = 480;
    static readonly string[] ServerOptions = ["bitwarden.com", "bitwarden.eu", "Self-hosted"];

    readonly BwClient bw;
    readonly AppSettings settings;
    readonly Updater updater;
    readonly Action<string, ToolTipIcon> notify;
    readonly bool preview;

    readonly DpiFont titleFont = new(Theme.Semibold, 15f);
    readonly DpiFont captionFont = new(Theme.Semibold, 8f);
    readonly DpiFont rowFont = new(Theme.Body, 10.5f);
    readonly DpiFont smallFont = new(Theme.Body, 9f);
    readonly DpiFont keyFont = new(Theme.Body, 8.5f);

    readonly FlatButton close, account, applyServer, updateAction;
    readonly ToggleSwitch startup, autoUpdate;
    readonly Segmented server;
    readonly TextField serverUrl;

    BwStatus? status;
    string accountMessage = "";
    bool accountError, busy;

    // Painted layout, computed by Relayout.
    int generalY, startupY, shortcutY, accountDividerY, accountY, accountStatusY, serverLabelY, messageY, updatesDividerY, updatesY, autoUpdateY, versionY;

    public SettingsForm(BwClient bw, AppSettings settings, Updater updater, Action<string, ToolTipIcon> notify, BwStatus? previewStatus = null)
    {
        this.bw = bw;
        this.settings = settings;
        this.updater = updater;
        this.notify = notify;
        preview = previewStatus != null;
        status = previewStatus;

        Text = "BwPicker settings";
        ShowInTaskbar = true;
        TopMost = false;

        close = new FlatButton(Theme, primary: false) { Text = "×", AccessibleName = "Close settings", TabStop = false };
        account = new FlatButton(Theme, primary: false) { Text = "Sign in" };
        applyServer = new FlatButton(Theme, primary: true) { Text = "Use this server", Visible = false };
        updateAction = new FlatButton(Theme, primary: false) { Text = "Check now" };
        startup = new ToggleSwitch(Theme) { AccessibleName = "Start with Windows" };
        autoUpdate = new ToggleSwitch(Theme) { AccessibleName = "Check for updates automatically" };
        server = new Segmented(Theme, ServerOptions) { AccessibleName = "Server" };
        serverUrl = new TextField(Theme, "https://vault.example.com") { Visible = false };
        foreach (var c in new Control[] { close, account, applyServer, updateAction, startup, autoUpdate, server, serverUrl }) c.BackColor = Theme.Background;
        Controls.AddRange([close, startup, account, server, serverUrl, applyServer, autoUpdate, updateAction]);

        startup.SetSilently(preview || Startup.Enabled);
        autoUpdate.SetSilently(settings.CheckForUpdates);
        ShowServer(status);

        close.Click += (_, _) => Close();
        startup.CheckedChanged += (_, _) => ToggleStartup();
        autoUpdate.CheckedChanged += (_, _) => { settings.CheckForUpdates = autoUpdate.Checked; TrySave(); };
        server.SelectedIndexChanged += (_, _) => { serverUrl.Visible = server.SelectedIndex == 2; ServerSelectionChanged(); Relayout(); };
        serverUrl.Box.TextChanged += (_, _) => ServerSelectionChanged();
        account.Click += async (_, _) => await AccountAction();
        applyServer.Click += async (_, _) => await ApplyServer();
        updateAction.Click += async (_, _) =>
        {
            if (updater.Available != null) await updater.Install();
            else await updater.Check(manual: true);
        };
        updater.Changed += OnUpdaterChanged;
        bw.Revoked += OnVaultChanged;
        FormClosed += (_, _) => { updater.Changed -= OnUpdaterChanged; bw.Revoked -= OnVaultChanged; };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MakeDraggable(this);
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Relayout(); });

        Relayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 3));
        Shown += async (_, _) => { Relayout(); Activate(); await RefreshAccount(); };
    }

    void Relayout()
    {
        SuspendLayout();
        foreach (var font in new[] { titleFont, captionFont, rowFont, smallFont, keyFont }) font.SetDpi(DeviceDpi);
        server.Font = smallFont;
        serverUrl.Font = rowFont;
        foreach (var b in new[] { close, account, applyServer, updateAction }) b.RestoreFont(DeviceDpi);

        int pad = S(24), width = S(Width0), right = width - pad, toggleW = S(40), toggleH = S(20);
        close.SetBounds(right - S(32), S(20), S(32), S(32));

        int y = S(76);
        generalY = y; y += S(26);
        startupY = y;
        startup.SetBounds(right - toggleW, y + S(9), toggleW, toggleH);
        y += S(50);
        shortcutY = y; y += S(50);
        accountDividerY = y + S(4); y += S(20);

        accountY = y; y += S(26);
        accountStatusY = y;
        account.SetBounds(right - S(112), y + S(2), S(112), S(34));
        y += S(50);
        serverLabelY = y; y += S(24);
        server.SetBounds(pad, y, width - pad * 2, S(36)); y += S(46);
        if (serverUrl.Visible) { serverUrl.SetBounds(pad, y, width - pad * 2, S(38)); y += S(48); }
        messageY = y;
        if (accountMessage.Length > 0) y += S(40);
        if (applyServer.Visible) { applyServer.SetBounds(right - S(180), y, S(180), S(34)); y += S(46); }
        updatesDividerY = y + S(4); y += S(20);

        updatesY = y; y += S(26);
        autoUpdateY = y;
        autoUpdate.SetBounds(right - toggleW, y + S(9), toggleW, toggleH);
        y += S(50);
        versionY = y;
        updateAction.SetBounds(right - S(132), y + S(2), S(132), S(34));
        y += S(48);

        ClientSize = new Size(width, y + S(12));
        UpdateUpdateButton();
        ResumeLayout(false);
        Invalidate();
    }

    async Task RefreshAccount()
    {
        if (preview) return;
        if (!TrustedCli.IsInstalled)
        {
            status = new BwStatus("unauthenticated", null, null);
            SetAccountMessage("The Bitwarden CLI isn't installed. Sign in to set it up.", error: false);
            ShowServer(status);
            return;
        }
        try { status = await bw.Status(); }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); }
        if (IsDisposed) return;
        ShowServer(status);
        Relayout();
    }

    void ShowServer(BwStatus? current)
    {
        var choice = ServerChoice.FromStatus(current?.ServerUrl);
        server.SetSilently((int)choice.Kind);
        serverUrl.Visible = choice.Kind == ServerKind.SelfHosted;
        serverUrl.Text = choice.Kind == ServerKind.SelfHosted ? choice.Url : "";
        account.Text = SignedIn ? "Sign out" : "Sign in";
        ServerSelectionChanged();
    }

    bool SignedIn => status is { Status: not "unauthenticated" };

    ServerChoice? SelectedServer()
    {
        return server.SelectedIndex switch
        {
            0 => new ServerChoice(ServerKind.BitwardenUs),
            1 => new ServerChoice(ServerKind.BitwardenEu),
            _ => TrySelfHosted(),
        };

        ServerChoice? TrySelfHosted()
        {
            try { return ServerChoice.SelfHosted(serverUrl.Text); }
            catch (InvalidOperationException) { return null; }
        }
    }

    void ServerSelectionChanged()
    {
        var current = ServerChoice.FromStatus(status?.ServerUrl);
        var selected = SelectedServer();
        bool changed = selected == null ? server.SelectedIndex == 2 && serverUrl.Text.Trim().Length > 0 : !selected.SameAs(current);
        bool wasVisible = applyServer.Visible;
        applyServer.Visible = changed;
        applyServer.Text = SignedIn ? "Sign out and switch" : "Use this server";
        applyServer.Enabled = !busy;
        if (changed && SignedIn)
            accountMessage = $"Switching servers signs you out of {status?.Email ?? "your account"}.";
        else if (!accountError)
            accountMessage = "";
        if (wasVisible != applyServer.Visible) Relayout(); else Invalidate();
    }

    async Task ApplyServer()
    {
        ServerChoice choice;
        try
        {
            choice = server.SelectedIndex == 2 ? ServerChoice.SelfHosted(serverUrl.Text) : SelectedServer()!;
        }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); return; }

        await RunBusy(async () =>
        {
            if (SignedIn) await bw.SignOut();
            await bw.SetServer(choice);
            status = await bw.Status();
            SetAccountMessage($"Now using {choice.DisplayName}. Sign in to continue.", error: false);
        });
        ShowServer(status);
        Relayout();
    }

    async Task AccountAction()
    {
        if (SignedIn)
        {
            await RunBusy(async () =>
            {
                await bw.SignOut();
                status = await bw.Status();
                SetAccountMessage("Signed out.", error: false);
            });
        }
        else
        {
            if (!TrayApp.EnsureCli(this)) return;
            var target = ServerChoice.FromStatus(status?.ServerUrl);
            using (var signIn = new SignInForm(bw, target.DisplayName))
                if (signIn.ShowDialog(this) != DialogResult.OK) return;
            if (!bw.Unlocked)
            {
                using var unlock = new UnlockForm(bw);
                unlock.ShowDialog(this);
            }
            status = null;
            await RefreshAccount();
            if (SignedIn) SetAccountMessage("", error: false);
        }
        ShowServer(status);
        Relayout();
    }

    async Task RunBusy(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        foreach (var c in new Control[] { account, applyServer, server, serverUrl }) c.Enabled = false;
        try { await action(); }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); }
        finally
        {
            busy = false;
            if (!IsDisposed) foreach (var c in new Control[] { account, applyServer, server, serverUrl }) c.Enabled = true;
        }
    }

    void SetAccountMessage(string text, bool error)
    {
        accountMessage = text;
        accountError = error && text.Length > 0;
        if (!IsDisposed) Relayout();
    }

    void ToggleStartup()
    {
        if (preview) return;
        try { Startup.Enabled = startup.Checked; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            startup.SetSilently(Startup.Enabled);
            notify("Windows didn't allow changing the startup setting.", ToolTipIcon.Warning);
        }
    }

    void TrySave()
    {
        if (preview) return;
        try { settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notify("Couldn't save settings.", ToolTipIcon.Warning); }
    }

    void OnUpdaterChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnUpdaterChanged(sender, e)); return; }
        UpdateUpdateButton();
        Invalidate();
    }

    void OnVaultChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnVaultChanged(sender, e)); return; }
        Invalidate();
    }

    void UpdateUpdateButton()
    {
        updateAction.Text = updater.Available is { } release ? $"Install {release.Version.ToString(3)}" : "Check now";
        updateAction.Enabled = !updater.Busy;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);
        int pad = S(24), width = ClientSize.Width, textWidth = width - pad * 2;

        Theme.FillRounded(g, Theme.Selected, new Rectangle(pad, S(20), S(32), S(32)), S(10));
        Theme.DrawShield(g, new Rectangle(pad + S(10), S(27), S(12), S(17)), Theme.Accent);
        DrawText(g, "Settings", titleFont, new Rectangle(S(68), S(20), S(300), S(32)), Theme.Text);

        Caption(g, "GENERAL", generalY);
        Row(g, startupY, "Start with Windows", Startup.AdminMode ? "As administrator, so it can type into admin apps" : "Open BwPicker in the tray when you sign in", textWidth - S(56));
        Row(g, shortcutY, "Shortcut", "Opens the picker over the app you're using", textWidth - S(140));
        DrawKeys(g, ["Ctrl", "Alt", "B"], width - pad, shortcutY + S(9));

        Divider(g, accountDividerY);
        Caption(g, "ACCOUNT", accountY);
        string who = status == null ? (preview ? "Preview" : "Checking…") : SignedIn ? status.Email ?? "Signed in" : "Not signed in";
        string where = status == null ? "" : SignedIn
            ? $"{(bw.Unlocked ? "Unlocked" : "Locked")} on {ServerChoice.FromStatus(status.ServerUrl).DisplayName}"
            : $"Server: {ServerChoice.FromStatus(status.ServerUrl).DisplayName}";
        Row(g, accountStatusY, who, where, textWidth - S(124));
        DrawText(g, "Server", smallFont, new Rectangle(pad, serverLabelY, textWidth, S(20)), Theme.SubtleText);
        if (accountMessage.Length > 0)
            TextRenderer.DrawText(g, accountMessage, smallFont, new Rectangle(pad, messageY, textWidth, S(36)),
                accountError ? Theme.Critical : Theme.SubtleText, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        Divider(g, updatesDividerY);
        Caption(g, "UPDATES", updatesY);
        Row(g, autoUpdateY, "Check for updates automatically", "Once a day, from GitHub Releases", textWidth - S(56));
        string updateStatus = updater.Status.Length > 0 ? updater.Status
            : settings.LastUpdateCheck is { } last ? $"Last checked {last.LocalDateTime:g}" : "Not checked yet";
        Row(g, versionY, $"Version {AppVersion.Text}", updateStatus, textWidth - S(144));
    }

    void Caption(Graphics g, string text, int y) =>
        DrawText(g, text, captionFont, new Rectangle(S(24), y, S(300), S(18)), Theme.SubtleText);

    void Row(Graphics g, int y, string title, string description, int width)
    {
        DrawText(g, title, rowFont, new Rectangle(S(24), y, width, S(22)), Theme.Text);
        DrawText(g, description, smallFont, new Rectangle(S(24), y + S(21), width, S(19)), Theme.SubtleText);
    }

    void Divider(Graphics g, int y)
    {
        using var pen = new Pen(Theme.Divider);
        g.DrawLine(pen, S(24), y, ClientSize.Width - S(24), y);
    }

    void DrawKeys(Graphics g, string[] keys, int right, int top)
    {
        const TextFormatFlags measure = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int h = S(22), gap = S(4);
        var widths = keys.Select(k => Math.Max(TextRenderer.MeasureText(k, keyFont, Size.Empty, measure).Width + S(14), h)).ToArray();
        int x = right - widths.Sum() - gap * (keys.Length - 1);
        for (int i = 0; i < keys.Length; i++)
        {
            var cap = new Rectangle(x, top, widths[i], h);
            Theme.FillRounded(g, Theme.Surface, cap, S(5));
            Theme.DrawRounded(g, Theme.Border, new RectangleF(cap.X + .5f, cap.Y + .5f, cap.Width - 1, cap.Height - 1), S(5));
            DrawText(g, keys[i], keyFont, cap, Theme.SubtleText, TextFormatFlags.HorizontalCenter);
            x = cap.Right + gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var font in new[] { titleFont, captionFont, rowFont, smallFont, keyFont }) font.Dispose();
        base.Dispose(disposing);
    }
}
