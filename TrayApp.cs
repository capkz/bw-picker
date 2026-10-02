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
    DateTime lastUsed = DateTime.Now;
    bool busy;

    public TrayApp()
    {
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
            Icon = SystemIcons.Shield,
            Text = $"BwPicker ({HotkeyLabel})",
            ContextMenuStrip = menu,
            Visible = true,
        };

        hotkeyWindow = new HotkeyWindow(id => { if (id == HotkeyId) _ = OnHotkey(); });
        if (!Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId, HotkeyModifiers, (uint)HotkeyKey))
            Notify($"{HotkeyLabel} is already used by another app.", ToolTipIcon.Warning);

        lockTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        lockTimer.Tick += async (_, _) =>
        {
            if (bw.Unlocked && !busy && DateTime.Now - lastUsed > AutoLockAfter) await Lock();
        };
        lockTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    async Task OnHotkey()
    {
        if (busy) return;
        busy = true;
        try
        {
            var target = WindowContext.From(Native.GetForegroundWindow());
            if (!bw.Unlocked && !await Unlock()) return;
            lastUsed = DateTime.Now;

            using var picker = new PickerForm(bw, target, Notify);
            picker.ShowDialog();
            lastUsed = DateTime.Now;
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

    async Task<bool> Unlock()
    {
        var status = await bw.Status();
        if (status.Status == "unauthenticated")
        {
            Notify("Log in once first: run `bw login` in a terminal.", ToolTipIcon.Warning);
            return false;
        }
        using var form = new UnlockForm(bw, status);
        return form.ShowDialog() == DialogResult.OK;
    }

    async Task Sync()
    {
        if (!bw.Unlocked)
        {
            Notify("Vault is locked; it syncs when you unlock.", ToolTipIcon.Info);
            return;
        }
        try
        {
            await bw.Load(sync: true);
            Notify($"Synced {bw.Entries.Count} logins.", ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex)
        {
            Notify(ex.Message, ToolTipIcon.Error);
        }
    }

    async Task Lock()
    {
        await bw.Lock();
    }

    async void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) await Lock();
    }

    async Task Shutdown()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        lockTimer.Stop();
        Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
        hotkeyWindow.Dispose();
        await bw.Lock();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    void Notify(string message, ToolTipIcon icon) => tray.ShowBalloonTip(4000, "BwPicker", message, icon);

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
