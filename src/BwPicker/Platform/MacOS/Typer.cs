namespace BwPicker;

/// <summary>
/// Types with CoreGraphics key events carrying the characters themselves, so every character (any layout, emoji)
/// types as is. macOS only allows this once the user has granted BwPicker Accessibility access.
/// </summary>
sealed class MacKeyboard : IKeyboard
{
    const ulong Modifiers = MacNative.ShiftFlag | MacNative.ControlFlag | MacNative.OptionFlag | MacNative.CommandFlag;
    char pendingHigh;

    public MacKeyboard()
    {
        if (!MacNative.AccessibilityAllowed(prompt: true))
            throw new InvalidOperationException("Allow BwPicker in System Settings → Privacy & Security → Accessibility, then try again. " +
                "(After an update, remove it there and add it again.)");
    }

    /// <summary>The frontmost app's process ID: the window handle on macOS.</summary>
    public IntPtr Foreground => MacNative.FrontmostApplication().Pid;

    public bool ModifiersDown => (MacNative.HeldModifiers & Modifiers) != 0;

    public bool Focus(IntPtr window) => MacNative.Activate((int)window);

    /// <summary>Keyboard focus is in that app (macOS routes keys to the focused app's key window).</summary>
    public bool FocusInside(IntPtr window) => MacNative.FocusedApplicationPid() == (int)window;

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);

    public bool Press(KeyStroke key)
    {
        switch (key.Special)
        {
            case SpecialKey.Tab: return MacNative.PostKey(MacNative.KeyTab, default);
            case SpecialKey.Enter: return MacNative.PostKey(MacNative.KeyReturn, default);
        }
        char c = key.Character;
        if (char.IsHighSurrogate(c)) { pendingHigh = c; return true; } // sent together with its low surrogate
        Span<char> text = stackalloc char[2];
        int length = 0;
        if (char.IsLowSurrogate(c) && pendingHigh != 0) text[length++] = pendingHigh;
        text[length++] = c;
        pendingHigh = '\0';
        try { return MacNative.PostKey(0, text[..length]); }
        finally { text.Clear(); }
    }
}

static partial class Typer
{
    private static partial IKeyboard CreateKeyboard(WindowContext target) => new MacKeyboard();

    /// <summary>BwPicker brings the destination app to the front itself before typing.</summary>
    public static bool ActivatesTarget => true;
}
