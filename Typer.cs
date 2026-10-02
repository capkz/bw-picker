using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>Types text into the focused window with SendInput.</summary>
static class Typer
{
    static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, LWin, RWin

    /// <summary>Brings the window to the front and confirms it actually got focus.</summary>
    public static bool FocusAndWait(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.SetForegroundWindow(hwnd);
        for (int i = 0; i < 40; i++)
        {
            if (Native.GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    /// <summary>A held Shift would turn Tab into Shift+Tab, so wait until the user lets go.</summary>
    public static void WaitForModifiersReleased()
    {
        for (int i = 0; i < 150; i++)
        {
            if (!Modifiers.Any(vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0)) return;
            Thread.Sleep(20);
        }
    }

    public static void TypeText(string text)
    {
        foreach (char c in text)
        {
            Send(Key(0, c, Native.KEYEVENTF_UNICODE), Key(0, c, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
            Thread.Sleep(4); // some apps drop characters that arrive in one burst
        }
    }

    public static void PressKey(ushort vk)
    {
        Send(Key(vk, '\0', 0), Key(vk, '\0', Native.KEYEVENTF_KEYUP));
        Thread.Sleep(30);
    }

    static Native.INPUT Key(ushort vk, char scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    static void Send(params Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
}
