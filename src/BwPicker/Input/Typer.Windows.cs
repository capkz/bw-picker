using System.Runtime.InteropServices;

namespace BwPicker;

sealed class WindowsKeyboard : IKeyboard
{
    static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    public IntPtr Foreground => Native.GetForegroundWindow();
    public bool ModifiersDown => Modifiers.Any(vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0);
    public bool Focus(IntPtr window) => Native.SetForegroundWindow(window);
    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
    public bool Press(KeyStroke key)
    {
        var inputs = new Native.INPUT[2];
        (ushort vk, char c, uint flags) = key.Special switch
        {
            SpecialKey.Tab => (Native.VK_TAB, '\0', 0u),
            SpecialKey.Enter => (Native.VK_RETURN, '\0', 0u),
            _ => ((ushort)0, key.Character, Native.KEYEVENTF_UNICODE),
        };
        inputs[0] = Input(vk, c, flags);
        inputs[1] = Input(vk, c, flags | Native.KEYEVENTF_KEYUP);
        try { return Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>()) == 2; }
        finally { Array.Clear(inputs); }
    }

    static Native.INPUT Input(ushort vk, char scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
    // Embedded browsers (CEF, WebView2) move focus between their own child windows while handling
    // input, sometimes on a different UI thread than the top-level window. Require focus to stay inside
    // the destination rather than on one exact child window.
    public bool FocusInside(IntPtr window)
    {
        IntPtr focus = FocusOf(Native.GetWindowThreadProcessId(window, out _));
        if (focus == IntPtr.Zero) focus = FocusOf(0); // 0 = the foreground thread
        return focus != IntPtr.Zero && (focus == window || Native.GetAncestor(focus, Native.GA_ROOT) == window);
    }

    static IntPtr FocusOf(uint thread)
    {
        var info = new Native.GUIThreadInfo { Size = (uint)Marshal.SizeOf<Native.GUIThreadInfo>() };
        return Native.GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }
}

static partial class Typer
{
    private static partial IKeyboard CreateKeyboard() => new WindowsKeyboard();
}
