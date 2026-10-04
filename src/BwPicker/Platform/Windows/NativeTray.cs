using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// Windows tray icon via Shell_NotifyIcon. Unlike Avalonia's tray icon it can show notifications, which Windows
/// renders as regular toasts (with BwPicker's name and icon, kept in the Notification Center). Must be created and
/// used on the UI thread, whose message loop delivers its window messages.
/// </summary>
sealed class NativeTray : IDisposable
{
    public sealed record MenuItem(string Text, Action? Action, bool IsDefault = false)
    {
        public static readonly MenuItem Separator = new("", null);
    }

    const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    const uint IconId = 1;
    const int WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, WM_COMMAND = 0x0111;
    const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    readonly WndProc wndProc; // kept alive: Windows calls it through a function pointer
    readonly IntPtr window, icon;
    readonly uint taskbarCreated;
    readonly string tooltip;
    readonly IReadOnlyList<MenuItem> menu;
    Action? balloonClick;
    bool disposed;

    public event Action? Clicked;

    public NativeTray(string tooltip, IReadOnlyList<MenuItem> menu)
    {
        this.tooltip = tooltip;
        this.menu = menu;
        wndProc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null), lpszClassName = "BwPickerTray",
        };
        RegisterClassEx(ref wc);
        // A hidden top-level window: message-only windows don't receive the taskbar's broadcasts.
        window = CreateWindowEx(0, "BwPickerTray", "BwPicker", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // The exe's own icon (ApplicationIcon is resource 32512), at the tray's size for this DPI.
        icon = LoadImage(wc.hInstance, new IntPtr(32512), 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50), 0);
        Add();
    }

    void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.szTip = tooltip;
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4: richer events, and toasts on Windows 10/11
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    /// <summary>Shows a Windows notification from the tray icon; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Notify(string title, string message, Notice kind, Action? onClick = null)
    {
        if (disposed) return;
        balloonClick = onClick;
        var data = Data(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = kind switch { Notice.Error => 3u, Notice.Warning => 2u, _ => 1u }; // NIIF_ERROR / WARNING / INFO
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window, uID = IconId, uFlags = flags,
        uCallbackMessage = CallbackMessage, hIcon = icon, szTip = "", szInfo = "", szInfoTitle = "",
    };

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT: Clicked?.Invoke(); break;
                case WM_CONTEXTMENU: ShowMenu(); break;
                case NIN_BALLOONUSERCLICK: var click = balloonClick; balloonClick = null; click?.Invoke(); break;
            }
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated && taskbarCreated != 0) { Add(); return IntPtr.Zero; } // Explorer restarted
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void ShowMenu()
    {
        IntPtr popup = CreatePopupMenu();
        try
        {
            for (int i = 0; i < menu.Count; i++)
            {
                var item = menu[i];
                if (ReferenceEquals(item, MenuItem.Separator)) AppendMenu(popup, 0x800 /* MF_SEPARATOR */, 0, null);
                else AppendMenu(popup, 0, (uint)(i + 1), item.Text);
                if (item.IsDefault) SetMenuDefaultItem(popup, (uint)(i + 1), 0);
            }
            GetCursorPos(out var point);
            SetForegroundWindow(window); // otherwise the menu doesn't close when clicking elsewhere
            int chosen = TrackPopupMenuEx(popup, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */, point.X, point.Y, window, IntPtr.Zero);
            PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= menu.Count) menu[chosen - 1].Action?.Invoke();
        }
        finally { DestroyMenu(popup); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        DestroyWindow(window);
    }

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
