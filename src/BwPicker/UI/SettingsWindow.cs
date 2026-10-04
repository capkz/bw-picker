using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BwPicker;

/// <summary>Startup, server/account and update settings.</summary>
sealed class SettingsWindow : PanelWindow
{
    static readonly string[] ServerOptions = ["bitwarden.com", "bitwarden.eu", "Self-hosted"];

    readonly BwClient bw;
    readonly AppSettings settings;
    readonly Updater updater;
    readonly Action<string, Notice> notify;
    readonly bool preview;

    readonly ToggleSwitch startup, autoUpdate;
    readonly ToggleButton[] server;
    readonly TextBox serverUrl;
    readonly Button account, applyServer, updateAction;
    readonly TextBlock who, where, accountMessage, versionStatus;
    BwStatus? status;
    bool accountError, busy;

    readonly ToggleSwitch admin;
    readonly Action<bool>? setAdmin;

    public SettingsWindow(BwClient bw, AppSettings settings, Updater updater, Action<string, Notice> notify,
        BwStatus? previewStatus = null, Action<bool>? setAdmin = null)
    {
        this.setAdmin = setAdmin;
        this.bw = bw;
        this.settings = settings;
        this.updater = updater;
        this.notify = notify;
        preview = previewStatus != null;
        status = previewStatus;
        Title = "BwPicker settings";
        Width = 480;
        SizeToContent = SizeToContent.Height;

        var close = new Button { Content = "×", Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        Ui.Neutral(P, close);
        close.Click += (_, _) => Close();

        startup = new ToggleSwitch { IsChecked = preview || Startup.Enabled, OnContent = null, OffContent = null };
        autoUpdate = new ToggleSwitch { IsChecked = settings.CheckForUpdates, OnContent = null, OffContent = null };
        startup.IsCheckedChanged += (_, _) => ToggleStartup();
        admin = new ToggleSwitch { IsChecked = preview || Startup.AdminMode, OnContent = null, OffContent = null };
        admin.IsCheckedChanged += (_, _) =>
        {
            if (preview || admin.IsChecked == Startup.AdminMode) return;
            admin.IsEnabled = false; // the app restarts in the other mode (or the toggle is reset if declined)
            setAdmin?.Invoke(admin.IsChecked == true);
        };
        autoUpdate.IsCheckedChanged += (_, _) => { settings.CheckForUpdates = autoUpdate.IsChecked == true; TrySave(); };

        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Keycap(P, "Ctrl"), Ui.Keycap(P, "Alt"), Ui.Keycap(P, "B") } };

        who = Ui.Text(preview ? "Preview" : "Checking…", 14, P.Text);
        where = Ui.Text("", 12.5, P.SubtleText);
        account = Ui.Button(P, "Sign in", primary: false);
        account.HorizontalAlignment = HorizontalAlignment.Right;
        account.MinWidth = 112;
        account.Click += async (_, _) => await AccountAction();

        server = ServerOptions.Select(o => new ToggleButton
        {
            Content = o, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 7), CornerRadius = new CornerRadius(6), FontSize = 12.5,
        }).ToArray();
        var segmented = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*") };
        for (int i = 0; i < server.Length; i++)
        {
            int index = i;
            Ui.NeutralToggle(P, server[i]);
            Grid.SetColumn(server[i], i * 2);
            segmented.Children.Add(server[i]);
            server[i].Click += (_, _) => SelectServer(index, fromUser: true);
        }
        serverUrl = Ui.Field(P, "https://vault.example.com");
        serverUrl.IsVisible = false;
        serverUrl.TextChanged += (_, _) => ServerSelectionChanged();
        accountMessage = Ui.Message(P);
        applyServer = Ui.Button(P, "Use this server", primary: true);
        applyServer.HorizontalAlignment = HorizontalAlignment.Right;
        applyServer.IsVisible = false;
        applyServer.Click += async (_, _) => await ApplyServer();

        versionStatus = Ui.Text("", 12.5, P.SubtleText);
        updateAction = Ui.Button(P, "Check now", primary: false);
        updateAction.HorizontalAlignment = HorizontalAlignment.Right;
        updateAction.MinWidth = 132;
        updateAction.Click += async (_, _) =>
        {
            if (updater.Available != null) await updater.Install();
            else await updater.Check(manual: true);
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 18), Spacing = 0,
            Children =
            {
                Ui.Header(P, "Settings", close, title: true),
                Caption("GENERAL"),
                Row(OperatingSystem.IsWindows() ? "Start with Windows" : "Start at sign-in",
                    Startup.AdminMode ? "As administrator, so it can type into admin apps" : "Open BwPicker in the tray when you sign in", startup),
                AdminRow(),
                Row("Shortcut", ShortcutHint, keys),
                Separator(),
                Caption("ACCOUNT"),
                Row(who, where, account),
                Small("Server"), segmented, Spaced(serverUrl, 8), Spaced(accountMessage, 6), Spaced(applyServer, 8),
                Separator(),
                Caption("UPDATES"),
                Row("Check for updates automatically", "Once a day, from GitHub Releases", autoUpdate),
                Row(Ui.Text($"Version {AppVersion.Text}", 14, P.Text), versionStatus, updateAction),
            },
        };

        ShowServer(status);
        UpdateUpdater();
        updater.Changed += OnUpdaterChanged;
        bw.Revoked += OnVaultChanged;
        Closed += (_, _) => { updater.Changed -= OnUpdaterChanged; bw.Revoked -= OnVaultChanged; };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
        Opened += async (_, _) => { Activate(); await RefreshAccount(); };
    }

    Control Caption(string text)
    {
        var t = Ui.Text(text, 11, P.SubtleText, FontWeight.SemiBold);
        t.Margin = new Thickness(0, 20, 0, 6);
        return t;
    }

    Control Small(string text)
    {
        var t = Ui.Text(text, 12.5, P.SubtleText);
        t.Margin = new Thickness(0, 12, 0, 6);
        return t;
    }

    Control Separator() { var d = Ui.Divider(P); d.Margin = new Thickness(0, 18, 0, 0); return d; }

    static Control Spaced(Control c, double top) { c.Margin = new Thickness(0, top, 0, 0); return c; }

    Control Row(string title, string description, Control right) =>
        Row(Ui.Text(title, 14, P.Text), Ui.Text(description, 12.5, P.SubtleText, wrap: true), right);

    static Control Row(TextBlock title, TextBlock description, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0), Children = { title, description } };
        right.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return grid;
    }

    /// <summary>Shows the actual mode again, e.g. after the UAC prompt was declined.</summary>
    public void RefreshAdmin()
    {
        admin.IsChecked = Startup.AdminMode;
        admin.IsEnabled = true;
    }

    bool SignedIn => status is { Status: not "unauthenticated" };

    async Task RefreshAccount()
    {
        if (preview) { ShowServer(status); return; }
        if (!TrustedCli.IsInstalled)
        {
            status = new BwStatus("unauthenticated", null, null);
            SetAccountMessage("The Bitwarden CLI isn't installed. Sign in to set it up.", error: false);
            ShowServer(status);
            return;
        }
        try { status = await bw.Status(); }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); }
        ShowServer(status);
    }

    /// <summary>Windows only: Linux (X11) has no equivalent restriction on typing into other apps.</summary>
    Control AdminRow()
    {
        var row = Row("Run as administrator", "Needed to type into apps that run as administrator, such as some game launchers. " +
            "Without it, BwPicker may not be able to fill those apps and you'd copy and paste instead.", admin);
        row.IsVisible = OperatingSystem.IsWindows();
        return row;
    }

    static string ShortcutHint => OperatingSystem.IsLinux() && !X11Session
        ? "On Wayland, bind it in your desktop's keyboard settings to: BwPicker --pick"
        : "Opens the picker over the app you're using";

#if LINUX
    static bool X11Session => X11.Available;
#else
    static bool X11Session => true;
#endif

    void ShowServer(BwStatus? current)
    {
        var choice = ServerChoice.FromStatus(current?.ServerUrl);
        SelectServer((int)choice.Kind, fromUser: false);
        serverUrl.Text = choice.Kind == ServerKind.SelfHosted ? choice.Url : "";
        account.Content = SignedIn ? "Sign out" : "Sign in";
        who.Text = current == null ? (preview ? "Preview" : "Checking…") : SignedIn ? current.Email ?? "Signed in" : "Not signed in";
        where.Text = current == null ? "" : SignedIn
            ? $"{(bw.Unlocked ? "Unlocked" : "Locked")} on {choice.DisplayName}"
            : $"Server: {choice.DisplayName}";
        ServerSelectionChanged();
    }

    int selectedServer;

    void SelectServer(int index, bool fromUser)
    {
        selectedServer = index;
        for (int i = 0; i < server.Length; i++) server[i].IsChecked = i == index;
        serverUrl.IsVisible = index == 2;
        if (fromUser) ServerSelectionChanged();
    }

    ServerChoice? SelectedServer()
    {
        if (selectedServer == 0) return new ServerChoice(ServerKind.BitwardenUs);
        if (selectedServer == 1) return new ServerChoice(ServerKind.BitwardenEu);
        try { return ServerChoice.SelfHosted(serverUrl.Text ?? ""); }
        catch (InvalidOperationException) { return null; }
    }

    void ServerSelectionChanged()
    {
        var current = ServerChoice.FromStatus(status?.ServerUrl);
        var selected = SelectedServer();
        bool changed = selected == null ? selectedServer == 2 && !string.IsNullOrWhiteSpace(serverUrl.Text) : !selected.SameAs(current);
        applyServer.IsVisible = changed;
        applyServer.Content = SignedIn ? "Sign out and switch" : "Use this server";
        applyServer.IsEnabled = !busy;
        if (changed && SignedIn) Ui.SetMessage(P, accountMessage, $"Switching servers signs you out of {status?.Email ?? "your account"}.", false);
        else if (!accountError) Ui.SetMessage(P, accountMessage, "", false);
    }

    async Task ApplyServer()
    {
        ServerChoice choice;
        try { choice = selectedServer == 2 ? ServerChoice.SelfHosted(serverUrl.Text ?? "") : SelectedServer()!; }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); return; }

        await RunBusy(async () =>
        {
            if (SignedIn) await bw.SignOut();
            await bw.SetServer(choice);
            status = await bw.Status();
            SetAccountMessage($"Now using {choice.DisplayName}. Sign in to continue.", error: false);
        });
        ShowServer(status);
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
            if (!await TrayController.EnsureCli()) return;
            var target = ServerChoice.FromStatus(status?.ServerUrl);
            if (!await new SignInWindow(bw, target.DisplayName).Run()) return;
            if (!bw.Unlocked) await new UnlockWindow(bw).Run();
            status = null;
            await RefreshAccount();
            if (SignedIn) SetAccountMessage("", error: false);
        }
        ShowServer(status);
    }

    async Task RunBusy(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        Control[] controls = [account, applyServer, serverUrl, .. server];
        foreach (var c in controls) c.IsEnabled = false;
        try { await action(); }
        catch (InvalidOperationException ex) { SetAccountMessage(ex.Message, error: true); }
        finally
        {
            busy = false;
            foreach (var c in controls) c.IsEnabled = true;
        }
    }

    void SetAccountMessage(string text, bool error)
    {
        accountError = error && text.Length > 0;
        Ui.SetMessage(P, accountMessage, text, error);
    }

    void ToggleStartup()
    {
        if (preview) return;
        try { Startup.Enabled = startup.IsChecked == true; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            startup.IsChecked = Startup.Enabled;
            notify("Couldn't change the startup setting. " + e.Message, Notice.Warning);
        }
    }

    void TrySave()
    {
        if (preview) return;
        try { settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notify("Couldn't save settings.", Notice.Warning); }
    }

    void OnUpdaterChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateUpdater);
    void OnVaultChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => ShowServer(status));

    void UpdateUpdater()
    {
        updateAction.Content = updater.Available is { } release ? $"Install {release.Version.ToString(3)}" : "Check now";
        if (updater.Available != null) updateAction.Classes.Add("accent"); else updateAction.Classes.Remove("accent");
        updateAction.IsEnabled = !updater.Busy;
        versionStatus.Text = updater.Status.Length > 0 ? updater.Status
            : settings.LastUpdateCheck is { } last ? $"Last checked {last.LocalDateTime:g}" : "Not checked yet";
    }
}
