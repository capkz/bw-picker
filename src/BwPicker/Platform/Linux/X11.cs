using System.Runtime.InteropServices;
using System.Text;

namespace BwPicker;

/// <summary>
/// The Xlib and XTest calls BwPicker needs on X11: reading the active window, grabbing the hotkey and typing. Each
/// user of a display connection opens its own; <see cref="Shared"/> serves the UI thread.
/// </summary>
static unsafe class X11
{
    const string Xlib = "libX11.so.6", Xtst = "libXtst.so.6";

    public const int KeyPress = 2, KeyRelease = 3;
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16;
    const int GrabModeAsync = 1;
    const uint XkbUseCoreKbd = 0x0100;

    /// <summary>X11 is usable: a display is set and this isn't a Wayland session (where X only sees XWayland apps).</summary>
    public static bool Available => !IsWayland && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };

    public static bool IsWayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

    static readonly Lazy<Display?> shared = new(() => Available ? Display.Open() : null);

    /// <summary>The UI thread's connection; null without X11.</summary>
    public static Display? Shared => shared.Value;

    /// <summary>An open connection to the X server. Not thread-safe: use each from one thread at a time.</summary>
    public sealed class Display : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public IntPtr Root { get; }
        readonly Dictionary<string, IntPtr> atoms = [];

        Display(IntPtr handle) { Handle = handle; Root = XDefaultRootWindow(handle); }

        public static Display? Open()
        {
            IntPtr handle = XOpenDisplay(IntPtr.Zero);
            return handle == IntPtr.Zero ? null : new Display(handle);
        }

        public IntPtr Atom(string name)
        {
            if (!atoms.TryGetValue(name, out var atom)) atoms[name] = atom = XInternAtom(Handle, name, false);
            return atom;
        }

        /// <summary>The window the window manager reports as active (_NET_ACTIVE_WINDOW).</summary>
        public IntPtr ActiveWindow => ReadLongs(Root, "_NET_ACTIVE_WINDOW", "WINDOW") is [var w, ..] ? new IntPtr(w) : IntPtr.Zero;

        public bool Exists(IntPtr window) => window != IntPtr.Zero && XGetWindowAttributes(Handle, window, out _) != 0;

        public uint? Pid(IntPtr window) => ReadLongs(window, "_NET_WM_PID", "CARDINAL") is [var pid, ..] && pid > 0 ? (uint)pid : null;

        public string Title(IntPtr window) =>
            ReadText(window, "_NET_WM_NAME", "UTF8_STRING", Encoding.UTF8) ?? ReadText(window, "WM_NAME", "STRING", Encoding.Latin1) ?? "";

        /// <summary>The WM_CLASS class name, e.g. "Mousepad" or "discord".</summary>
        public string ClassName(IntPtr window)
        {
            string? value = ReadText(window, "WM_CLASS", "STRING", Encoding.Latin1);
            if (value == null) return "";
            var parts = value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1] : parts.FirstOrDefault() ?? "";
        }

        /// <summary>The window's rectangle in root (screen) coordinates.</summary>
        public ScreenRect? Bounds(IntPtr window)
        {
            if (XGetWindowAttributes(Handle, window, out var attributes) == 0) return null;
            if (!XTranslateCoordinates(Handle, window, Root, 0, 0, out int x, out int y, out _)) return null;
            return new ScreenRect(x, y, x + attributes.width, y + attributes.height);
        }

        public (int X, int Y)? Pointer() =>
            XQueryPointer(Handle, Root, out _, out _, out int x, out int y, out _, out _, out _) ? (x, y) : null;

        /// <summary>The window with keyboard focus (not necessarily a top-level window); zero for none or PointerRoot.</summary>
        public IntPtr Focus()
        {
            XGetInputFocus(Handle, out IntPtr focus, out _);
            return focus.ToInt64() is 0 or 1 ? IntPtr.Zero : focus;
        }

        /// <summary>True if <paramref name="window"/> is <paramref name="ancestor"/> or inside it.</summary>
        public bool IsInside(IntPtr window, IntPtr ancestor)
        {
            for (int depth = 0; window != IntPtr.Zero && depth < 64; depth++)
            {
                if (window == ancestor) return true;
                if (window == Root || XQueryTree(Handle, window, out _, out IntPtr parent, out IntPtr children, out _) == 0) return false;
                if (children != IntPtr.Zero) XFree(children);
                window = parent;
            }
            return false;
        }

        /// <summary>Asks the window manager to activate <paramref name="window"/> (EWMH, as a pager would).</summary>
        public void Activate(IntPtr window)
        {
            var e = new XEvent();
            e.type = 33; // ClientMessage
            e.client.window = window;
            e.client.message_type = Atom("_NET_ACTIVE_WINDOW");
            e.client.format = 32;
            e.client.l0 = 2; // source: pager, which window managers honour without focus-stealing checks
            e.client.l1 = 0; // CurrentTime
            XSendEvent(Handle, Root, false, (IntPtr)((1 << 19) | (1 << 20)) /* SubstructureRedirect | SubstructureNotify */, ref e);
            XFlush(Handle);
        }

        long[]? ReadLongs(IntPtr window, string property, string type)
        {
            if (XGetWindowProperty(Handle, window, Atom(property), IntPtr.Zero, 64, false, Atom(type),
                    out _, out int format, out IntPtr count, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return null;
            try
            {
                if (format != 32) return null;
                var values = new long[(int)count];
                Marshal.Copy(data, values, 0, values.Length); // format 32 is returned as C longs
                return values;
            }
            finally { XFree(data); }
        }

        string? ReadText(IntPtr window, string property, string type, Encoding encoding)
        {
            if (XGetWindowProperty(Handle, window, Atom(property), IntPtr.Zero, 4096, false, Atom(type),
                    out _, out int format, out IntPtr count, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return null;
            try { return format == 8 ? encoding.GetString((byte*)data, (int)count) : null; }
            finally { XFree(data); }
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            XCloseDisplay(Handle);
            Handle = IntPtr.Zero;
        }
    }

    // Grabbing a key another client already holds fails asynchronously with BadAccess; catch it during the grab.
    static volatile int lastError;

    [UnmanagedCallersOnly]
    static int RecordError(IntPtr display, XErrorEvent* error)
    {
        lastError = error->error_code;
        return 0;
    }

    /// <summary>Grabs <paramref name="keycode"/> with <paramref name="modifiers"/> on the root window, whatever Caps/Num Lock are.</summary>
    public static bool GrabKey(Display display, int keycode, uint modifiers)
    {
        lastError = 0;
        var previous = XSetErrorHandler((IntPtr)(delegate* unmanaged<IntPtr, XErrorEvent*, int>)&RecordError);
        try
        {
            foreach (uint locks in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
                XGrabKey(display.Handle, keycode, modifiers | locks, display.Root, false, GrabModeAsync, GrabModeAsync);
            XSync(display.Handle, false);
        }
        finally { XSetErrorHandler(previous); }
        if (lastError == 0) return true;
        UngrabKey(display, keycode, modifiers);
        return false;
    }

    public static void UngrabKey(Display display, int keycode, uint modifiers)
    {
        foreach (uint locks in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
            XUngrabKey(display.Handle, keycode, modifiers | locks, display.Root);
        XSync(display.Handle, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XErrorEvent
    {
        public int type;
        public IntPtr display, resourceid, serial;
        public byte error_code, request_code, minor_code;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display, window, root, subwindow, time;
        public int x, y, x_root, y_root;
        public uint state, keycode;
        public int same_screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClientMessageEvent
    {
        public int type;
        public IntPtr serial;
        public int send_event;
        public IntPtr display, window, message_type;
        public int format;
        public long l0, l1, l2, l3, l4;
    }

    /// <summary>The XEvent union (24 longs).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XKeyEvent key;
        [FieldOffset(0)] public XClientMessageEvent client;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual, root;
        public int @class, bit_gravity, win_gravity, backing_store;
        public IntPtr backing_planes, backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed, map_state;
        public IntPtr all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XkbStateRec
    {
        public byte group, locked_group;
        public ushort base_group, latched_group;
        public byte mods, base_mods, latched_mods, locked_mods, compat_state;
        public byte grab_mods, compat_grab_mods, lookup_mods, compat_lookup_mods;
        public ushort ptr_buttons;
    }

    [DllImport(Xlib)] public static extern int XInitThreads();
    [DllImport(Xlib)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Xlib)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(Xlib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Xlib)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(Xlib)] static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Xlib)]
    static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, long length, bool delete,
        IntPtr type, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(Xlib)] public static extern int XFree(IntPtr data);
    [DllImport(Xlib)] static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);
    [DllImport(Xlib)] static extern bool XTranslateCoordinates(IntPtr display, IntPtr from, IntPtr to, int x, int y, out int toX, out int toY, out IntPtr child);
    [DllImport(Xlib)]
    static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child, out int rootX, out int rootY,
        out int x, out int y, out uint mask);
    [DllImport(Xlib)] static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport(Xlib)] static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);
    [DllImport(Xlib)] static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr mask, ref XEvent e);
    [DllImport(Xlib)] public static extern int XFlush(IntPtr display);
    [DllImport(Xlib)] public static extern int XSync(IntPtr display, bool discard);
    [DllImport(Xlib)] static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(Xlib)] static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Xlib)] static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport(Xlib)] public static extern int XPending(IntPtr display);
    [DllImport(Xlib)] public static extern int XNextEvent(IntPtr display, out XEvent e);
    [DllImport(Xlib)] public static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(Xlib)] public static extern int XQueryKeymap(IntPtr display, byte* keys);
    [DllImport(Xlib)] public static extern int XDisplayKeycodes(IntPtr display, out int min, out int max);
    [DllImport(Xlib)] public static extern IntPtr XGetKeyboardMapping(IntPtr display, byte first, int count, out int keysymsPerKeycode);
    [DllImport(Xlib)] public static extern int XChangeKeyboardMapping(IntPtr display, int first, int keysymsPerKeycode, IntPtr* keysyms, int count);
    [DllImport(Xlib)] public static extern IntPtr XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);
    [DllImport(Xlib)] public static extern int XkbGetState(IntPtr display, uint device, out XkbStateRec state);
    [DllImport(Xlib)] public static extern bool XkbLockModifiers(IntPtr display, uint device, uint affect, uint values);
    [DllImport(Xlib)] public static extern bool XkbSetDetectableAutoRepeat(IntPtr display, bool detectable, out bool supported);
    [DllImport(Xtst)] public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, IntPtr delay);

    public static uint CoreKeyboard => XkbUseCoreKbd;
}
