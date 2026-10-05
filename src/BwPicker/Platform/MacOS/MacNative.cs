using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// The macOS system calls BwPicker needs: the Objective-C runtime (NSWorkspace, NSPasteboard, NSRunningApplication),
/// CoreFoundation strings, the Accessibility API (the focused window and its title), CoreGraphics key events and
/// Carbon hot keys. Objective-C objects are plain pointers here; messages go through objc_msgSend with exact
/// signatures, which is required on Apple silicon.
/// </summary>
static unsafe class MacNative
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    const string LibSystem = "/usr/lib/libSystem.dylib";

    static MacNative()
    {
        // Avalonia loads AppKit too, but don't depend on load order for NSWorkspace and NSPasteboard.
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    // ---- Objective-C ----

    public static IntPtr Class(string name) => objc_getClass(name);
    public static IntPtr Selector(string name) => sel_registerName(name);

    /// <summary>Sends a message that returns an object (or nothing).</summary>
    public static IntPtr Send(IntPtr target, string selector) => objc_msgSend(target, Selector(selector));
    public static IntPtr Send(IntPtr target, string selector, IntPtr arg) => objc_msgSend(target, Selector(selector), arg);
    public static IntPtr Send(IntPtr target, string selector, IntPtr arg1, IntPtr arg2) => objc_msgSend(target, Selector(selector), arg1, arg2);
    public static int SendInt(IntPtr target, string selector) => objc_msgSend_int(target, Selector(selector));
    public static long SendLong(IntPtr target, string selector) => objc_msgSend_long(target, Selector(selector));
    public static bool SendBool(IntPtr target, string selector, ulong arg) => objc_msgSend_bool_ulong(target, Selector(selector), arg) != 0;

    /// <summary>An NSString with these characters (caller releases it). Built from UTF-16 directly, without a .NET string.</summary>
    public static IntPtr NSString(ReadOnlySpan<char> text)
    {
        fixed (char* chars = text)
            return objc_msgSend_chars(objc_msgSend(Class("NSString"), Selector("alloc")), Selector("initWithCharacters:length:"), chars, (nuint)text.Length);
    }

    public static void Release(IntPtr obj) { if (obj != IntPtr.Zero) objc_msgSend(obj, Selector("release")); }

    /// <summary>Runs <paramref name="action"/> inside an autorelease pool (calls from threads AppKit didn't start need one).</summary>
    public static T WithPool<T>(Func<T> action)
    {
        IntPtr pool = objc_autoreleasePoolPush();
        try { return action(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // ---- CoreFoundation (NSString and CFString are the same objects) ----

    public static IntPtr CFString(string text)
    {
        fixed (char* chars = text) return CFStringCreateWithCharacters(IntPtr.Zero, chars, text.Length);
    }

    public static string? ToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero || CFGetTypeID(cfString) != CFStringGetTypeID()) return null;
        nint length = CFStringGetLength(cfString);
        var buffer = new char[length];
        fixed (char* chars = buffer) CFStringGetCharacters(cfString, new CFRange(0, length), chars);
        return new string(buffer);
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CFRange(nint Location, nint Length);

    // ---- Accessibility ----

    /// <summary>
    /// Whether macOS lets BwPicker read other apps' windows and send keystrokes (System Settings → Privacy &amp; Security →
    /// Accessibility). With <paramref name="prompt"/>, macOS shows its dialog pointing the user there.
    /// </summary>
    public static bool AccessibilityAllowed(bool prompt)
    {
        if (!prompt) return AXIsProcessTrusted() != 0;
        IntPtr cf = NativeLibrary.Load(CoreFoundation);
        IntPtr key = CFString("AXTrustedCheckOptionPrompt");
        IntPtr trueValue = *(IntPtr*)NativeLibrary.GetExport(cf, "kCFBooleanTrue");
        IntPtr keyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
        IntPtr valueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        IntPtr options = CFDictionaryCreate(IntPtr.Zero, &key, &trueValue, 1, keyCallbacks, valueCallbacks);
        try { return AXIsProcessTrustedWithOptions(options) != 0; }
        finally { CFRelease(options); CFRelease(key); }
    }

    /// <summary>An attribute of an accessibility element (caller releases it), or zero.</summary>
    public static IntPtr Attribute(IntPtr element, string name)
    {
        IntPtr attribute = CFString(name);
        try { return AXUIElementCopyAttributeValue(element, attribute, out IntPtr value) == 0 ? value : IntPtr.Zero; }
        finally { CFRelease(attribute); }
    }

    /// <summary>The focused window of the app with this process ID (caller releases it), or zero.</summary>
    public static IntPtr FocusedWindow(int pid)
    {
        IntPtr app = AXUIElementCreateApplication(pid);
        if (app == IntPtr.Zero) return IntPtr.Zero;
        try { return Attribute(app, "AXFocusedWindow"); }
        finally { CFRelease(app); }
    }

    /// <summary>The title of the app's focused window, or null without Accessibility access.</summary>
    public static string? FocusedWindowTitle(int pid)
    {
        IntPtr window = FocusedWindow(pid);
        if (window == IntPtr.Zero) return null;
        try
        {
            IntPtr title = Attribute(window, "AXTitle");
            try { return ToManaged(title) ?? ""; }
            finally { if (title != IntPtr.Zero) CFRelease(title); }
        }
        finally { CFRelease(window); }
    }

    /// <summary>The process ID of the app whose control has keyboard focus, or 0.</summary>
    public static int FocusedApplicationPid()
    {
        IntPtr system = AXUIElementCreateSystemWide();
        try
        {
            IntPtr app = Attribute(system, "AXFocusedApplication");
            if (app == IntPtr.Zero) return 0;
            try { return AXUIElementGetPid(app, out int pid) == 0 ? pid : 0; }
            finally { CFRelease(app); }
        }
        finally { CFRelease(system); }
    }

    /// <summary>Screen rectangle (points, top-left origin) of the app's focused window.</summary>
    public static (double X, double Y, double Width, double Height)? FocusedWindowFrame(int pid)
    {
        IntPtr window = FocusedWindow(pid);
        if (window == IntPtr.Zero) return null;
        try
        {
            IntPtr position = Attribute(window, "AXPosition"), size = Attribute(window, "AXSize");
            try
            {
                CGPoint point;
                CGSize extent;
                if (position == IntPtr.Zero || size == IntPtr.Zero ||
                    AXValueGetValue(position, 1 /* kAXValueCGPointType */, &point) == 0 ||
                    AXValueGetValue(size, 2 /* kAXValueCGSizeType */, &extent) == 0) return null;
                return (point.X, point.Y, extent.Width, extent.Height);
            }
            finally
            {
                if (position != IntPtr.Zero) CFRelease(position);
                if (size != IntPtr.Zero) CFRelease(size);
            }
        }
        finally { CFRelease(window); }
    }

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }

    // ---- Workspace ----

    /// <summary>The frontmost app: process ID and display name.</summary>
    public static (int Pid, string Name) FrontmostApplication() => WithPool(() =>
    {
        IntPtr workspace = Send(Class("NSWorkspace"), "sharedWorkspace");
        IntPtr app = Send(workspace, "frontmostApplication");
        if (app == IntPtr.Zero) return (0, "");
        return (SendInt(app, "processIdentifier"), ToManaged(Send(app, "localizedName")) ?? "");
    });

    /// <summary>The main display's backing scale factor (points to pixels).</summary>
    public static double MainScreenScale() => WithPool(() =>
    {
        IntPtr screen = Send(Class("NSScreen"), "mainScreen");
        return screen == IntPtr.Zero ? 1 : objc_msgSend_double(screen, Selector("backingScaleFactor"));
    });

    /// <summary>Brings the app with this process ID to the front.</summary>
    public static bool Activate(int pid) => WithPool(() =>
    {
        IntPtr app = Send(Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", (IntPtr)pid);
        return app != IntPtr.Zero && SendBool(app, "activateWithOptions:", 2 /* NSApplicationActivateIgnoringOtherApps */);
    });

    // ---- CoreGraphics ----

    public const ushort KeyTab = 48, KeyReturn = 36;
    public const ulong ShiftFlag = 0x20000, ControlFlag = 0x40000, OptionFlag = 0x80000, CommandFlag = 0x100000;

    /// <summary>Modifier keys physically held right now.</summary>
    public static ulong HeldModifiers => CGEventSourceFlagsState(1 /* kCGEventSourceStateHIDSystemState */);

    /// <summary>Presses and releases a key; with <paramref name="text"/>, the key types those characters instead.</summary>
    public static bool PostKey(ushort virtualKey, ReadOnlySpan<char> text)
    {
        foreach (bool down in new[] { true, false })
        {
            IntPtr e = CGEventCreateKeyboardEvent(IntPtr.Zero, virtualKey, down);
            if (e == IntPtr.Zero) return false;
            try
            {
                CGEventSetFlags(e, 0); // held modifiers mustn't change what's typed
                if (!text.IsEmpty) fixed (char* chars = text) CGEventKeyboardSetUnicodeString(e, (nuint)text.Length, chars);
                CGEventPost(0 /* kCGHIDEventTap */, e);
            }
            finally { CFRelease(e); }
        }
        return true;
    }

    /// <summary>The mouse position in points (top-left origin).</summary>
    public static CGPoint MouseLocation()
    {
        IntPtr e = CGEventCreate(IntPtr.Zero);
        try { return CGEventGetLocation(e); }
        finally { if (e != IntPtr.Zero) CFRelease(e); }
    }

    /// <summary>Whether the login session's screen is locked.</summary>
    public static bool ScreenLocked()
    {
        IntPtr session = CGSessionCopyCurrentDictionary();
        if (session == IntPtr.Zero) return false;
        IntPtr key = CFString("CGSSessionScreenIsLocked");
        try
        {
            IntPtr value = CFDictionaryGetValue(session, key);
            return value != IntPtr.Zero && CFBooleanGetValue(value) != 0;
        }
        finally { CFRelease(key); CFRelease(session); }
    }

    /// <summary>Milliseconds of uptime, not counting sleep (CLOCK_UPTIME_RAW).</summary>
    public static long UptimeMilliseconds() => (long)(clock_gettime_nsec_np(8) / 1_000_000);

    // ---- Processes ----

    /// <summary>proc_pidinfo(PROC_PIDTBSDINFO): start time and effective user of a process.</summary>
    public static bool ProcessBsdInfo(int pid, out long startedMicroseconds, out uint uid)
    {
        byte* info = stackalloc byte[136]; // struct proc_bsdinfo
        if (proc_pidinfo(pid, 3, 0, info, 136) != 136) { startedMicroseconds = 0; uid = 0; return false; }
        uid = *(uint*)(info + 20);
        startedMicroseconds = (long)(*(ulong*)(info + 120) * 1_000_000 + *(ulong*)(info + 128));
        return true;
    }

    public static string ProcessPath(int pid)
    {
        byte* path = stackalloc byte[4096];
        int length = proc_pidpath(pid, path, 4096);
        return length > 0 ? System.Text.Encoding.UTF8.GetString(path, length) : "";
    }

    // ---- Carbon hot keys ----

    [StructLayout(LayoutKind.Sequential)] public struct EventTypeSpec { public uint EventClass, EventKind; }
    [StructLayout(LayoutKind.Sequential)] public struct EventHotKeyID { public uint Signature, Id; }

    [DllImport(Carbon)] public static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] public static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint count, EventTypeSpec* types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] public static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)] public static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(Carbon)] public static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    // ---- imports ----

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector);
    [DllImport(ObjC)] static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector, IntPtr arg);
    [DllImport(ObjC)] static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern int objc_msgSend_int(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern long objc_msgSend_long(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern double objc_msgSend_double(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern byte objc_msgSend_bool_ulong(IntPtr target, IntPtr selector, ulong arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr objc_msgSend_chars(IntPtr target, IntPtr selector, char* chars, nuint length);
    [DllImport(ObjC)] static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(ObjC)] static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr obj);
    [DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint length);
    [DllImport(CoreFoundation)] static extern nint CFStringGetLength(IntPtr str);
    [DllImport(CoreFoundation)] static extern void CFStringGetCharacters(IntPtr str, CFRange range, char* buffer);
    [DllImport(CoreFoundation)] static extern nuint CFGetTypeID(IntPtr obj);
    [DllImport(CoreFoundation)] static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)] static extern byte CFBooleanGetValue(IntPtr boolean);

    [DllImport(ApplicationServices)] static extern byte AXIsProcessTrusted();
    [DllImport(ApplicationServices)] static extern byte AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(ApplicationServices)] static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(ApplicationServices)] static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(ApplicationServices)] static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(ApplicationServices)] static extern byte AXValueGetValue(IntPtr value, int type, void* result);

    [DllImport(CoreGraphics)] static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);
    [DllImport(CoreGraphics)] static extern void CGEventKeyboardSetUnicodeString(IntPtr e, nuint length, char* text);
    [DllImport(CoreGraphics)] static extern void CGEventSetFlags(IntPtr e, ulong flags);
    [DllImport(CoreGraphics)] static extern void CGEventPost(uint tap, IntPtr e);
    [DllImport(CoreGraphics)] static extern ulong CGEventSourceFlagsState(int stateId);
    [DllImport(CoreGraphics)] static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CoreGraphics)] static extern CGPoint CGEventGetLocation(IntPtr e);
    [DllImport(CoreGraphics)] static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport(LibSystem)] static extern int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int size);
    [DllImport(LibSystem)] static extern int proc_pidpath(int pid, byte* buffer, uint size);
    [DllImport(LibSystem)] static extern ulong clock_gettime_nsec_np(int clock);
}
