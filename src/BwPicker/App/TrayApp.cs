using Microsoft.Win32;

namespace BwPicker;

sealed class TrayApp : ApplicationContext
{
    const int HotkeyId = 1;
    const uint HotkeyModifiers = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
    const Keys HotkeyKey = Keys.B;
    const string HotkeyLabel = "Ctrl+Alt+B";
    static readonly TimeSpan AutoLockAfter = TimeSpan.FromMinutes(15);

    readonly BwClient bw;
    readonly AppSettings settings = AppSettings.Load();
    readonly Updater updater;
    readonly System.Windows.Forms.Timer updateTimer;
    SettingsForm? settingsForm;
    Version? announcedUpdate;
    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkeyWindow;
    readonly System.Windows.Forms.Timer lockTimer;
    readonly Control dispatcher = new();
    long lastUsed = Environment.TickCount64;
    volatile bool shuttingDown, sessionLocked, suspended;
    bool busy;
    bool syncing;

    /// <param name="client">An alternative vault client; by default the real Bitwarden CLI.</param>
    public TrayApp(BwClient? client = null)
    {
        bw = client ?? new();
        _ = dispatcher.Handle;
        updater = new Updater(settings);
        updater.ReadyToInstall += (_, exe) => InstallUpdate(exe);

        var menu = new ContextMenuStrip();
        var settingsItem = menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        menu.Items.Add("Check for updates", null, async (_, _) => { await updater.Check(manual: true); AnnounceUpdate(manual: true); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sync vault", null, async (_, _) => await Sync());
        menu.Items.Add("Lock", null, async (_, _) => await Lock());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await Shutdown());

        tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = $"BwPicker ({HotkeyLabel})",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowSettings();
        tray.BalloonTipClicked += (_, _) => { if (announcedUpdate != null) ShowSettings(); };

        hotkeyWindow = new HotkeyWindow(id => { if (id == HotkeyId) OnHotkey(); });
        if (!Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId, HotkeyModifiers, (uint)HotkeyKey))
            Notify($"{HotkeyLabel} is already used by another app.", ToolTipIcon.Warning);

        lockTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        lockTimer.Tick += async (_, _) =>
        {
            if (bw.Unlocked && Environment.TickCount64 - lastUsed > AutoLockAfter.TotalMilliseconds) await Lock();
        };
        lockTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        using var warmFont = Theme.Body(9.5f, 96);
        _ = WarmStatus();

        // First check shortly after startup, then hourly to see whether the daily check is due.
        updateTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = 60 * 60 * 1000;
            if (!updater.CheckIsDue) return;
            await updater.Check(manual: false);
            AnnounceUpdate(manual: false);
        };
        updateTimer.Start();

        if (!settings.Welcomed)
        {
            Notify($"BwPicker is in your tray. Press {HotkeyLabel} over a login screen; double-click the icon for settings.", ToolTipIcon.Info);
            settings.Welcomed = true;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void ShowSettings()
    {
        if (shuttingDown) return;
        if (settingsForm is { IsDisposed: false })
        {
            if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
            return;
        }
        settingsForm = new SettingsForm(bw, settings, updater, Notify);
        settingsForm.FormClosed += (_, _) => { settingsForm?.Dispose(); settingsForm = null; };
        settingsForm.Show();
    }

    void AnnounceUpdate(bool manual)
    {
        if (updater.Available is { } release && (manual || announcedUpdate != release.Version))
        {
            announcedUpdate = release.Version;
            Notify($"BwPicker {release.Version.ToString(3)} is available. Click here or open Settings to install it.", ToolTipIcon.Info);
        }
        else if (manual) Notify(updater.Status, ToolTipIcon.Info);
    }

    async void InstallUpdate(string exe)
    {
        try
        {
            Updater.InstallAndRelaunch(exe);
            await Shutdown();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Notify("Update failed. " + e.Message, ToolTipIcon.Error);
            await updater.Check(manual: false);
        }
    }

    async Task WarmStatus()
    {
        try { await bw.Status(); }
        catch (InvalidOperationException) { /* The unlock window reports errors when opened. */ }
    }

    void OnHotkey()
    {
        if (busy || shuttingDown || sessionLocked || suspended) return;
        busy = true;
        try
        {
            var target = WindowContext.From(Native.GetForegroundWindow());
            bool needsUnlock = !bw.Unlocked;
            if (needsUnlock && !Unlock()) return;
            lastUsed = Environment.TickCount64;
            if (needsUnlock) _ = Sync(quiet: true);

            using var picker = new PickerForm(bw, target, Notify);
            picker.ShowDialog();
            lastUsed = Environment.TickCount64;
        }
        catch (InvalidOperationException ex)
        {
            Notify(ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            busy = false;
        }
    }

    bool Unlock()
    {
        if (!EnsureCli()) return false;
        if (bw.CachedStatus?.Status != "unauthenticated")
        {
            using var form = new UnlockForm(bw);
            var result = form.ShowDialog();
            if (result != DialogResult.Retry) return result == DialogResult.OK; // Retry: the CLI isn't signed in
        }
        using (var signIn = new SignInForm(bw, ServerChoice.FromStatus(bw.CachedStatus?.ServerUrl).DisplayName))
            if (signIn.ShowDialog() != DialogResult.OK) return false;
        if (bw.Unlocked) return true;
        using var unlock = new UnlockForm(bw); // API key sign-in leaves the vault locked
        return unlock.ShowDialog() == DialogResult.OK;
    }

    /// <summary>Offers to install the Bitwarden CLI when it's missing; false if it still isn't there.</summary>
    internal static bool EnsureCli(IWin32Window? owner = null)
    {
        if (TrustedCli.IsInstalled) return true;
        using var setup = new CliSetupForm();
        return setup.ShowDialog(owner) == DialogResult.OK && TrustedCli.IsInstalled;
    }

    async Task Sync(bool quiet = false)
    {
        if (syncing) return;
        if (!bw.Unlocked)
        {
            if (!quiet) Notify("Vault is locked; it syncs when you unlock.", ToolTipIcon.Info);
            return;
        }
        syncing = true;
        try
        {
            await bw.Load(sync: true);
            if (!quiet && bw.Unlocked) Notify($"Synced {bw.Entries.Count} logins.", ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex)
        {
            Notify(quiet ? "Background sync failed; using the local vault. " + ex.Message : ex.Message, ToolTipIcon.Warning);
        }
        finally { syncing = false; }
    }

    async Task Lock()
    {
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException ex) { Notify(ex.Message, ToolTipIcon.Warning); }
    }

    async void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            sessionLocked = true;
            await BlockAccess();
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or
            SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            sessionLocked = false;
            if (!suspended) bw.AllowInteraction();
        }
    }

    async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { suspended = true; await BlockAccess(); }
        else if (e.Mode == PowerModes.Resume) { suspended = false; if (!sessionLocked) bw.AllowInteraction(); }
    }

    async Task BlockAccess()
    {
        try { var task = bw.Block(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException ex) { Notify(ex.Message, ToolTipIcon.Warning); }
    }

    async Task Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        lockTimer.Stop();
        updateTimer.Stop();
        settingsForm?.Close();
        Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
        hotkeyWindow.Dispose();
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException) { }
        finally { bw.Dispose(); tray.Visible = false; tray.Dispose(); ExitThread(); }
    }

    void Notify(string message, ToolTipIcon icon)
    {
        if (shuttingDown || dispatcher.IsDisposed) return;
        if (dispatcher.InvokeRequired) { dispatcher.BeginInvoke(() => Notify(message, icon)); return; }
        tray.ShowBalloonTip(4000, "BwPicker", message, icon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shuttingDown = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            bw.Dispose(); SecureClipboard.ClearOwned();
            lockTimer.Dispose(); updateTimer.Dispose(); tray.Dispose(); hotkeyWindow.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        static readonly IntPtr HWND_MESSAGE = new(-3);
        readonly Action<int> onHotkey;

        public HotkeyWindow(Action<int> onHotkey)
        {
            this.onHotkey = onHotkey;
            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
