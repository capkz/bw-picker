using Avalonia.Threading;

namespace BwPicker;

/// <summary>The tray icon, global hotkey, auto-lock and update checks; owns the vault client.</summary>
sealed class TrayController : IDisposable
{
    static readonly string HotkeyLabel = Shortcuts.Hotkey;
    static readonly TimeSpan AutoLockAfter = TimeSpan.FromMinutes(15);

    readonly BwClient bw;
    readonly AppSettings settings = AppSettings.Load();
    readonly Updater updater;
    readonly NativeTray tray;
    readonly GlobalHotkey hotkey;
    readonly SessionMonitor session = new();
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

        tray = new NativeTray($"BwPicker ({HotkeyLabel})",
        [
            new("Settings…", ShowSettings, IsDefault: true),
            new("Check for updates", async () => { await updater.Check(manual: true); AnnounceUpdate(manual: true); }),
            NativeTray.MenuItem.Separator,
            new("Sync vault", async () => await Sync()),
            new("Lock", async () => { if (await Lock()) Notify("Vault locked.", Notice.Info); }),
            NativeTray.MenuItem.Separator,
            new("Exit", async () => await Shutdown()),
        ]);
        tray.Clicked += ShowSettings;

        hotkey = new GlobalHotkey();
        hotkey.Pressed += () => Dispatcher.UIThread.Post(() => _ = OnHotkey());
        if (!hotkey.Registered) Notify(hotkey.Problem ?? $"{HotkeyLabel} is already used by another app.", Notice.Warning);
        hotkey.Unavailable += message => Notify(message, Notice.Warning);

        lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        lockTimer.Tick += async (_, _) =>
        {
            if (bw.Unlocked && !busy && Environment.TickCount64 - lastUsed > AutoLockAfter.TotalMilliseconds) await Lock();
        };
        lockTimer.Start();

        session.Locked += async () => { sessionLocked = true; await BlockAccess(); };
        session.Unlocked += () => { sessionLocked = false; if (!suspended) bw.AllowInteraction(); };
        session.Suspending += async () => { suspended = true; await BlockAccess(); };
        session.Resumed += () => { suspended = false; if (!sessionLocked) bw.AllowInteraction(); };
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

        // Coming back from a switch between normal and administrator mode: show where the user left off.
        if (settings.ReopenSettingsAfterSwitch)
        {
            settings.ReopenSettingsAfterSwitch = false;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Notify(Startup.AdminMode ? "BwPicker is now running as administrator." : "BwPicker is now running without administrator rights.", Notice.Info);
            Dispatcher.UIThread.Post(ShowSettings, DispatcherPriority.Background);
        }
#if WINDOWS
        // "Run as administrator" is on by default: a release build that isn't elevated yet asks once (UAC).
        else if (settings.RunAsAdmin && !Startup.AdminMode && !AppVersion.IsDevelopment)
        {
            Notify("BwPicker needs administrator rights to type into apps that run as administrator. Approve the Windows prompt, " +
                "or turn it off in Settings.", Notice.Info);
            var askLater = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            askLater.Tick += (_, _) => { askLater.Stop(); SetAdmin(true); };
            askLater.Start();
        }
#endif

        if (!settings.Welcomed)
        {
            Notify($"BwPicker is in your tray. Press {HotkeyLabel} over a login screen; click the icon for settings.", Notice.Info);
            settings.Welcomed = true;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void ShowSettings()
    {
        if (shuttingDown) return;
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
#if WINDOWS
        settingsWindow = new SettingsWindow(bw, settings, updater, Notify, setAdmin: SetAdmin);
#else
        settingsWindow = new SettingsWindow(bw, settings, updater, Notify);
#endif
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

#if WINDOWS
    /// <summary>Switches administrator mode; on success this instance exits and the switched copy takes over.</summary>
    void SetAdmin(bool enable)
    {
        settings.RunAsAdmin = enable;
        try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (enable == Startup.AdminMode) return;
        try
        {
            if (enable)
            {
                if (!AdminInstall.RequestElevation())
                {
                    settings.RunAsAdmin = false;
                    settings.ReopenSettingsAfterSwitch = false;
                    try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    Notify("Administrator mode stays off, so BwPicker can't type into apps that run as administrator. " +
                        "You can turn it on in Settings.", Notice.Warning);
                    settingsWindow?.RefreshAdmin();
                    return;
                }
            }
            else AdminInstall.LeaveAdminMode();
            settings.ReopenSettingsAfterSwitch = true;
            try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _ = Shutdown();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Notify("Couldn't switch administrator mode. " + e.Message, Notice.Error);
            settingsWindow?.RefreshAdmin();
        }
    }
#endif

    void AnnounceUpdate(bool manual)
    {
        if (updater.Available is { } release && (manual || announcedUpdate != release.Version))
        {
            announcedUpdate = release.Version;
            Notify($"BwPicker {release.Version.ToString(3)} is available. Click here or open Settings to install it.", Notice.Info, ShowSettings);
        }
        else if (manual) Notify(updater.Status, Notice.Info);
    }

    async void InstallUpdate(UpdatePayload payload)
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
            var target = WindowContext.FromForeground();
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

    async Task<bool> Lock()
    {
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; return true; }
        catch (InvalidOperationException ex) { Notify(ex.Message, Notice.Warning); return false; }
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
        session.Dispose();
        lockTimer.Stop();
        updateTimer.Stop();
        settingsWindow?.Close();
        hotkey.Dispose();
        try { var task = bw.Lock(); SecureClipboard.ClearOwned(); await task; }
        catch (InvalidOperationException) { }
        finally { bw.Dispose(); tray.Dispose(); shutdownApp(); }
    }

    void Notify(string message, Notice kind) => Notify(message, kind, null);

    void Notify(string message, Notice kind, Action? onClick)
    {
        if (shuttingDown || message.Length == 0) return;
        Dispatcher.UIThread.Post(() => tray.Notify("BwPicker", message, kind, onClick));
    }

    public void Dispose()
    {
        shuttingDown = true;
        session.Dispose();
        bw.Dispose();
        SecureClipboard.ClearOwned();
        hotkey.Dispose();
        tray.Dispose();
    }
}
