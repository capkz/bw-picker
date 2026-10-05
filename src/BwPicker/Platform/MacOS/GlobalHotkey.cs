using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// Control+Option+B (the Mac's Ctrl+Alt+B) through Carbon's RegisterEventHotKey, which macOS allows without any
/// permission. Must be created on the main thread (Avalonia's UI thread on macOS); <see cref="Pressed"/> is raised there.
/// </summary>
sealed unsafe class GlobalHotkey : IDisposable
{
    const uint KeyB = 11; // kVK_ANSI_B
    const uint ControlKey = 0x1000, OptionKey = 0x0800;
    const uint KeyboardClass = 0x6B657962; // 'keyb'
    const uint HotKeyPressed = 5;

    static GlobalHotkey? current; // Carbon calls back into a static function
    readonly IntPtr handler, hotKey;

    public event Action? Pressed;

    /// <summary>Not raised on macOS; the hotkey either registers at startup or not at all.</summary>
    public event Action<string>? Unavailable { add { } remove { } }

    public GlobalHotkey()
    {
        current = this;
        var spec = new MacNative.EventTypeSpec { EventClass = KeyboardClass, EventKind = HotKeyPressed };
        IntPtr target = MacNative.GetApplicationEventTarget();
        if (MacNative.InstallEventHandler(target, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHotKey, 1, &spec, IntPtr.Zero, out handler) != 0)
            return;
        var id = new MacNative.EventHotKeyID { Signature = 0x42775069 /* 'BwPi' */, Id = 1 };
        Registered = MacNative.RegisterEventHotKey(KeyB, ControlKey | OptionKey, id, target, 0, out hotKey) == 0;
    }

    /// <summary>False if another app already owns Control+Option+B.</summary>
    public bool Registered { get; }

    public string? Problem => null;

    [UnmanagedCallersOnly]
    static int OnHotKey(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        current?.Pressed?.Invoke();
        return 0; // noErr: handled
    }

    public void Dispose()
    {
        if (hotKey != IntPtr.Zero) MacNative.UnregisterEventHotKey(hotKey);
        if (handler != IntPtr.Zero) MacNative.RemoveEventHandler(handler);
        current = null;
    }
}
