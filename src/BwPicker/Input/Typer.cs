using System.Runtime.InteropServices;

namespace BwPicker;

interface IKeyboard
{
    IntPtr Foreground { get; }
    bool ModifiersDown { get; }
    bool Focus(IntPtr window);
    /// <summary>True while keyboard focus is the window itself or any control inside it.</summary>
    bool FocusInside(IntPtr window);
    uint Send(Native.INPUT[] inputs);
    void Wait(int milliseconds);
}

sealed class WindowsKeyboard : IKeyboard
{
    static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    public IntPtr Foreground => Native.GetForegroundWindow();
    public bool ModifiersDown => Modifiers.Any(vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0);
    public bool Focus(IntPtr window) => Native.SetForegroundWindow(window);
    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
    public uint Send(Native.INPUT[] inputs) => Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
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

/// <summary>Which credential fields to type. Two-step logins ask for the username and password on separate pages.</summary>
enum TypeFields { Both, UsernameOnly, PasswordOnly }

/// <summary>Stops if focus, destination identity, modifier state, or the vault lease changes.</summary>
sealed class InputTyper(IKeyboard keyboard)
{
    public void Type(IntPtr window, CredentialLease credential, bool submit, Func<bool> originalIdentity, Func<bool> stillMatches,
        TypeFields fields = TypeFields.Both)
    {
        if (window == IntPtr.Zero || !credential.IsValid || !originalIdentity())
            throw new InvalidOperationException("The destination window could not be verified.");
        Validate(credential.Username.AsSpan());
        if (credential.Password is { } available) Validate(available.Characters);
        keyboard.Focus(window);
        int tries = 0;
        while (keyboard.Foreground != window && tries++ < 40) keyboard.Wait(25);
        tries = 0;
        while (keyboard.ModifiersDown && tries++ < 150) keyboard.Wait(20);
        if (keyboard.ModifiersDown) throw new InvalidOperationException("Held modifier keys prevented typing. Release them and try again.");
        // Chromium/Electron apps briefly report no focused control right after being reactivated.
        // Wait for focus to settle first; nothing is sent while it is outside the window.
        tries = 0;
        while (!keyboard.FocusInside(window) && tries++ < 40) keyboard.Wait(25);
        bool FocusSettles()
        {
            for (int i = 0; i < 8; i++)
            {
                if (keyboard.FocusInside(window)) return true;
                keyboard.Wait(25);
            }
            return false;
        }
        void Guard()
        {
            string? reason =
                !credential.IsValid ? "the vault was locked" :
                keyboard.Foreground != window ? "another window came to the front" :
                keyboard.ModifiersDown ? "Ctrl, Alt, Shift or Win was held down" :
                !stillMatches() ? "the app's window changed (title or process)" :
                !FocusSettles() ? "keyboard focus left the app's window" :
                null;
            if (reason != null) throw new InvalidOperationException($"Typing stopped: {reason}.");
        }
        var inputs = new Native.INPUT[2];
        void SendKey(ushort vk, char c, uint flags)
        {
            Guard();
            inputs[0] = Key(vk, c, flags);
            inputs[1] = Key(vk, c, flags | Native.KEYEVENTF_KEYUP);
            try
            {
                if (keyboard.Send(inputs) != 2) throw new InvalidOperationException("Windows blocked credential typing.");
            }
            finally { Array.Clear(inputs); }
        }
        void Text(ReadOnlySpan<char> text)
        {
            foreach (char c in text) { SendKey(0, c, Native.KEYEVENTF_UNICODE); keyboard.Wait(4); }
        }
        Guard();
        bool typeUsername = fields != TypeFields.PasswordOnly && !string.IsNullOrEmpty(credential.Username);
        if (typeUsername) Text(credential.Username);
        if (fields == TypeFields.Both && typeUsername)
        {
            SendKey(Native.VK_TAB, '\0', 0);
            keyboard.Wait(30);
        }
        if (fields != TypeFields.UsernameOnly && credential.Password is { } password) Text(password.Characters);
        if (submit) SendKey(Native.VK_RETURN, '\0', 0);
    }
    static void Validate(ReadOnlySpan<char> text)
    {
        if (text.Length > 4096) throw new InvalidOperationException("This credential is too long for safe automatic typing.");
        foreach (char c in text)
            if (char.IsControl(c)) throw new InvalidOperationException("This credential contains control characters and cannot be typed safely.");
    }
    static Native.INPUT Key(ushort vk, char scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}

static class Typer
{
    public static void TypeCredentials(WindowContext target, CredentialLease credentials, bool submit, TypeFields fields) =>
        new InputTyper(new WindowsKeyboard()).Type(target.Handle, credentials, submit, target.HasOriginalIdentity, target.WindowStillMatches, fields);
}
