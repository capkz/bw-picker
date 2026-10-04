using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Win32;

namespace BwPicker;

/// <summary>The tray icon, global hotkey, auto-lock and update checks; owns the vault client.</summary>
sealed class TrayController : IDisposable
{
    const uint HotkeyModifiers = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
    const uint HotkeyKey = 0x42; // B
    const string HotkeyLabel = "Ctrl+Alt+B";
    static readonly TimeSpan AutoLockAfter = TimeSpan.FromMinutes(15);

    readonly BwClient bw;
    readonly AppSettings settings = AppSettings.Load();
    readonly Updater updater;
    readonly TrayIcon tray;
    readonly GlobalHotkey hotkey;
    readonly DispatcherTimer lockTimer, updateTimer;
    readonly Action shutdownApp;
    SettingsWindow? settingsWindow;
    Version? announcedUpdate;
    long lastUsed = Environment.TickCount64;
    volatile bool shuttingDown, sessionLocked, suspended;
    bool busy, syncing;

    /// <param name="client">An alternative vault client; by default the real Bitwarden CLI.</param>
    public TrayController(Action shutdownApp, BwClient? client = null)
    {
        this.shutdownApp = shutdownApp;
        bw = client ?? new();
        updater = new Updater(settings);
        updater.ReadyToInstall += (_, payload) => Dispatcher.UIThread.Post(() => InstallUpdate(payload));

        var menu = new NativeMenu();
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(Item("Check for updates", async () => { await updater.Check(manual: true); AnnounceUpdate(manual: true); }));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Sync vault", async () => await Sync()));
        menu.Items.Add(Item("Lock", async () => await Lock()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Exit", async () => await Shutdown()));
        tray = new TrayIcon { Icon = Ui.AppIcon, ToolTipText = $"BwPicker ({HotkeyLabel})", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowSettings();
        // Tray icons are owned by the application; register it so the platform keeps it alive and shown.
        if (Avalonia.Application.Current is { } app) TrayIcon.SetIcons(app, [tray]);

        hotkey = new GlobalHotkey(HotkeyModifiers, HotkeyKey);
        hotkey.Pressed += () => Dispatcher.UIThread.Post(() => _ = OnHotkey());
        if (!hotkey.Registered) Notify($"{HotkeyLabel} is already used by another app.", Notice.Warning);

        lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        lockTimer.Tick += async (_, _) =>
        {
            if (bw.Unlocked && !busy && Environment.TickCount64 - lastUsed > AutoLockAfter.TotalMilliseconds) await Lock();
        };
        lockTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = WarmStatus();

        // First check shortly after startup, then hourly to see whether the daily check is due.
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(1);
            if (!updater.CheckIsDue) return;
            await updater.Check(manual: false);
            AnnounceUpdate(manual: false);
        };
        updateTimer.Start();

        if (!settings.Welcomed)
        {
            Notify($"BwPicker is in your tray. Press {HotkeyLabel} over a login screen; click the icon for settings.", Notice.Info);
            settings.Welcomed = true;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    void ShowSettings()
    {
        if (shuttingDown) return;
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(bw, settings, updater, Notify);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    void AnnounceUpdate(bool manual)
    {
        if (updater.Available is { } release && (manual || announcedUpdate != release.Version))
        {
            announcedUpdate = release.Version;
            Notify($"BwPicker {release.Version.ToString(3)} is available. Click here or open Settings to install it.", Notice.Info, ShowSettings);
        }
        else if (manual) Notify(updater.Status, Notice.Info);
    }

    async void InstallUpdate(string payload)
    {
        try
        {
            Updater.InstallAndRelaunch(payload);
            await Shutdown();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Notify("Update failed. " + e.Message, Notice.Error);
            await updater.Check(manual: false);
        }
    }

    async Task WarmStatus()
    {
        try { await bw.Status(); }
        catch (InvalidOperationException) { /* The unlock window reports errors when opened. */ }
    }

    async Task OnHotkey()
    {
        if (busy || shuttingDown || sessionLocked || suspended) return;
        busy = true;
        try
        {
            var target = WindowContext.From(Native.GetForegroundWindow());
            bool needsUnlock = !bw.Unlocked;
            if (needsUnlock && !await Unlock()) return;
            lastUsed = Environment.TickCount64;
            if (needsUnlock) _ = Sync(quiet: true);

            await new PickerWindow(bw, target, Notify).ShowAndWait(() => true);
            lastUsed = Environment.TickCount64;
        }
        catch (InvalidOperationException ex) { Notify(ex.Message, Notice.Error); }
        finally { busy = false; }
    }

    async Task<bool> Unlock()
    {
        if (!await EnsureCli()) return false;
        if (bw.CachedStatus?.Status != "unauthenticated")
        {
            var result = await new UnlockWindow(bw).Run();
            if (result != UnlockResult.NeedsSignIn) return result == UnlockResult.Unlocked;
        }
        if (!await new SignInWindow(bw, ServerChoice.FromStatus(bw.CachedStatus?.ServerUrl).DisplayName).Run()) return false;
        if (bw.Unlocked) return true;
        return await new UnlockWindow(bw).Run() == UnlockResult.Unlocked; // API key sign-in leaves the vault locked
    }

    /// <summary>Offers to install the Bitwarden CLI when it's missing; false if it still isn't there.</summary>
    internal static async Task<bool> EnsureCli()
    {
        if (TrustedCli.IsInstalled) return true;
        return await new CliSetupWindow().Ask() && TrustedCli.IsInstalled;
    }

    async Task Sync(bool quiet = false)
    {
        if (syncing) return;
        if (!bw.Unlocked)
        {
            if (!quiet) Notify("Vault is locked; it syncs when you unlock.", Notice.Info);
            return;
        }
        syncing = true;
        try
        {
            await bw.Load(sync: true);
            if (!quiet && bw.Unlocked) Notify($"Synced {bw.Entries.Count} logins.", Notice.Info);
        }
        catch (InvalidOperationException ex)
        {
            Notify(quiet ? "Background sync failed; using the local vault. " + ex.Message : ex.Message, Notice.Warning);
        }
        finally { syncing = false; }
    }

    async Task Lock()
    {
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException ex) { Notify(ex.Message, Notice.Warning); }
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
        catch (InvalidOperationException ex) { Notify(ex.Message, Notice.Warning); }
    }

    async Task Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        lockTimer.Stop();
        updateTimer.Stop();
        settingsWindow?.Close();
        hotkey.Dispose();
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException) { }
        finally { bw.Dispose(); tray.IsVisible = false; tray.Dispose(); shutdownApp(); }
    }

    void Notify(string message, Notice kind) => Notify(message, kind, null);

    void Notify(string message, Notice kind, Action? onClick)
    {
        if (shuttingDown || message.Length == 0) return;
        Dispatcher.UIThread.Post(() => Toast.Show(message, kind, onClick));
    }

    public void Dispose()
    {
        shuttingDown = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        bw.Dispose();
        SecureClipboard.ClearOwned();
        hotkey.Dispose();
        tray.Dispose();
    }
}
