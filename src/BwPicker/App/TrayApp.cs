using Microsoft.Win32;

namespace BwPicker;

sealed class TrayApp : ApplicationContext
{
    const int HotkeyId = 1;
    const uint HotkeyModifiers = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
    const Keys HotkeyKey = Keys.B;
    const string HotkeyLabel = "Ctrl+Alt+B";
    static readonly TimeSpan AutoLockAfter = TimeSpan.FromMinutes(15);

    readonly BwClient bw = new();
    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkeyWindow;
    readonly System.Windows.Forms.Timer lockTimer;
    readonly Control dispatcher = new();
    long lastUsed = Environment.TickCount64;
    volatile bool shuttingDown, sessionLocked, suspended;
    bool busy;
    bool syncing;

    public TrayApp()
    {
        _ = dispatcher.Handle;
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Startup.Enabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Startup.Enabled = startup.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Sync vault", null, async (_, _) => await Sync());
        menu.Items.Add("Lock", null, async (_, _) => await Lock());
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await Shutdown());

        tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = $"BwPicker ({HotkeyLabel})",
            ContextMenuStrip = menu,
            Visible = true,
        };

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
        using var form = new UnlockForm(bw);
        return form.ShowDialog() == DialogResult.OK;
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
            lockTimer.Dispose(); tray.Dispose(); hotkeyWindow.Dispose(); dispatcher.Dispose();
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

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BwPicker";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
