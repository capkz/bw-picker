namespace BwPicker;

/// <summary>
/// Types through XTest. Characters on the current layout use their own key (with Shift when needed). Others get
/// an unused keycode each, mapped for the duration of the typing and cleared afterwards: reusing one keycode for
/// several characters races with apps that read the new mapping late (GTK 4 would type the last one every time).
/// Caps Lock is switched off while typing so letters keep their case, then restored.
/// </summary>
sealed unsafe class X11Keyboard : IKeyboard, IDisposable
{
    static readonly long[] ModifierKeysyms =
    [
        0xffe1, 0xffe2, // Shift
        0xffe3, 0xffe4, // Control
        0xffe9, 0xffea, // Alt
        0xffeb, 0xffec, // Super
        0xffe7, 0xffe8, // Meta
        0xfe03,         // ISO_Level3_Shift (AltGr)
    ];

    readonly X11.Display x;
    readonly int minCode, maxCode;
    readonly Dictionary<long, (byte Code, bool Shift)> keys = [];
    readonly byte[] modifierCodes;
    readonly byte shiftCode;
    readonly bool capsWasLocked;
    readonly Queue<byte> unused = new();
    readonly Dictionary<long, byte> mapped = [];
    readonly List<byte> mappedOrder = [];
    char pendingHigh;

    public X11Keyboard()
    {
        x = X11.Shared ?? throw new InvalidOperationException(X11.IsWayland
            ? "Typing into other apps isn't available on Wayland. Use Ctrl+U / Ctrl+P to copy instead."
            : "Typing needs an X11 session.");
        IntPtr h = x.Handle;
        X11.XDisplayKeycodes(h, out minCode, out maxCode);
        X11.XkbGetState(h, X11.CoreKeyboard, out var state);
        for (int code = minCode; code <= maxCode; code++)
        {
            long plain = X11.XkbKeycodeToKeysym(h, (byte)code, state.group, 0);
            if (plain is >= 0xff80 and <= 0xffbd) continue; // keypad keys depend on Num Lock
            for (int level = 0; level < 2; level++)
            {
                long sym = level == 0 ? plain : X11.XkbKeycodeToKeysym(h, (byte)code, state.group, 1);
                if (sym != 0) keys.TryAdd(sym, ((byte)code, level == 1));
            }
        }
        modifierCodes = ModifierKeysyms.Select(s => X11.XKeysymToKeycode(h, (IntPtr)s)).Where(c => c != 0).ToArray();
        shiftCode = X11.XKeysymToKeycode(h, (IntPtr)0xffe1);
        if ((state.locked_mods & X11.LockMask) != 0)
        {
            capsWasLocked = X11.XkbLockModifiers(h, X11.CoreKeyboard, X11.LockMask, 0);
            X11.XSync(h, false);
        }
    }

    public IntPtr Foreground => x.ActiveWindow;

    public bool ModifiersDown
    {
        get
        {
            byte* state = stackalloc byte[32];
            X11.XQueryKeymap(x.Handle, state);
            return modifierCodes.Any(code => (state[code / 8] & (1 << (code % 8))) != 0);
        }
    }

    public bool Focus(IntPtr window) { x.Activate(window); return true; }

    public bool FocusInside(IntPtr window) => x.Focus() is var focus && focus != IntPtr.Zero && x.IsInside(focus, window);

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);

    public bool Press(KeyStroke key)
    {
        long sym;
        switch (key.Special)
        {
            case SpecialKey.Tab: sym = 0xff09; break;
            case SpecialKey.Enter: sym = 0xff0d; break;
            default:
                char c = key.Character;
                if (char.IsHighSurrogate(c)) { pendingHigh = c; return true; } // typed with its low surrogate
                int codePoint = char.IsLowSurrogate(c) && pendingHigh != 0 ? char.ConvertToUtf32(pendingHigh, c) : c;
                pendingHigh = '\0';
                // Latin-1 keysyms equal the code point; everything else uses the Unicode keysym range.
                sym = codePoint is >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff ? codePoint : 0x01000000 | codePoint;
                break;
        }
        var (code, shift) = keys.TryGetValue(sym, out var known) ? known : (MapSpare(sym), false);
        IntPtr h = x.Handle;
        bool ok = true;
        if (shift) ok &= X11.XTestFakeKeyEvent(h, shiftCode, true, IntPtr.Zero) != 0;
        ok &= X11.XTestFakeKeyEvent(h, code, true, IntPtr.Zero) != 0;
        ok &= X11.XTestFakeKeyEvent(h, code, false, IntPtr.Zero) != 0;
        if (shift) ok &= X11.XTestFakeKeyEvent(h, shiftCode, false, IntPtr.Zero) != 0;
        X11.XSync(h, false);
        return ok;
    }

    /// <summary>The keycode typing <paramref name="sym"/>: an unused one, mapped to it on first use.</summary>
    byte MapSpare(long sym)
    {
        if (mapped.TryGetValue(sym, out byte code)) return code;
        if (mappedOrder.Count == 0) foreach (byte free in FindUnusedKeycodes()) unused.Enqueue(free);
        if (unused.Count > 0) code = unused.Dequeue();
        else
        {
            // More distinct characters than spare keys: reuse the oldest, once apps have certainly handled it.
            code = mappedOrder[0];
            mappedOrder.RemoveAt(0);
            foreach (var old in mapped.Where(m => m.Value == code).ToList()) mapped.Remove(old.Key);
            Thread.Sleep(150);
        }
        IntPtr* syms = stackalloc IntPtr[2];
        syms[0] = syms[1] = (IntPtr)sym;
        X11.XChangeKeyboardMapping(x.Handle, code, 2, syms, 1);
        X11.XSync(x.Handle, false);
        mapped[sym] = code;
        mappedOrder.Add(code);
        Thread.Sleep(50); // let apps pick up the new mapping before the key arrives
        return code;
    }

    IEnumerable<byte> FindUnusedKeycodes()
    {
        IntPtr map = X11.XGetKeyboardMapping(x.Handle, (byte)minCode, maxCode - minCode + 1, out int perCode);
        if (map == IntPtr.Zero) throw new InvalidOperationException("Couldn't read the keyboard layout.");
        var free = new List<byte>();
        try
        {
            var syms = (IntPtr*)map;
            for (int code = maxCode; code >= minCode; code--)
            {
                bool isUnused = true;
                for (int i = 0; i < perCode && isUnused; i++) isUnused = syms[(code - minCode) * perCode + i] == IntPtr.Zero;
                if (isUnused) free.Add((byte)code);
            }
        }
        finally { X11.XFree(map); }
        if (free.Count == 0)
            throw new InvalidOperationException("A character in this login isn't on your keyboard layout. Copy it instead (Ctrl+U / Ctrl+P).");
        return free;
    }

    public void Dispose()
    {
        IntPtr h = x.Handle;
        if (mappedOrder.Count > 0)
        {
            Thread.Sleep(150); // the app may still be handling the last keys; clear the mapping after them
            IntPtr* none = stackalloc IntPtr[2];
            none[0] = none[1] = IntPtr.Zero;
            foreach (byte code in mappedOrder) X11.XChangeKeyboardMapping(h, code, 2, none, 1);
        }
        if (capsWasLocked) X11.XkbLockModifiers(h, X11.CoreKeyboard, X11.LockMask, X11.LockMask);
        X11.XSync(h, false);
    }
}

static partial class Typer
{
    private static partial IKeyboard CreateKeyboard(WindowContext target) => X11.Available ? new X11Keyboard() : new WaylandKeyboard(target);

    /// <summary>
    /// On X11 BwPicker activates the destination itself. Wayland doesn't allow that, so the picker hides first and the
    /// desktop gives focus back to the window that had it.
    /// </summary>
    public static bool ActivatesTarget => X11.Available;
}
