using Tmds.DBus.Protocol;

namespace BwPicker;

/// <summary>
/// Typing on Wayland through the RemoteDesktop portal: the desktop asks the user once to allow BwPicker to control
/// the keyboard, and the approval is kept (a restore token in settings), so later typing starts without a prompt.
/// A session is opened only while typing, because desktops show a "remote control" indicator while one exists.
/// </summary>
sealed class WaylandInput : IDisposable
{
    const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    readonly DBusConnection bus;
    readonly string session;

    WaylandInput(DBusConnection bus, string session) { this.bus = bus; this.session = session; }

    /// <summary>The desktop offers keyboard control through the portal (GNOME 46+, KDE Plasma 6, Hyprland…).</summary>
    public static bool Available
    {
        get
        {
            if (!X11.IsWayland) return false;
            try
            {
                return Task.Run(async () => await Portal.Connect() is { } bus && await Portal.Version(bus, RemoteDesktop) > 0)
                    .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            catch (TimeoutException) { return false; }
        }
    }

    /// <summary>Whether the user has already allowed keyboard control (so typing won't show a prompt).</summary>
    public static bool Allowed => AppSettings.Load().RemoteDesktopToken is { Length: > 0 };

    /// <summary>Starts a keyboard session, asking the user the first time. Throws if it isn't allowed.</summary>
    public static async Task<WaylandInput> Open()
    {
        var bus = await Portal.Connect().ConfigureAwait(false) ?? throw new InvalidOperationException("The desktop portal isn't available.");
        string token = Portal.Token("rd"), sessionToken = Portal.Token("rds");
        var (code, results) = await Portal.Request(bus, token, Portal.Call(bus, RemoteDesktop, "CreateSession", "a{sv}", (ref MessageWriter w) => Portal.WriteOptions(ref w, 
            [Portal.Option("handle_token", token), Portal.Option("session_handle_token", sessionToken)])), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (code != 0 || !results.TryGetValue("session_handle", out var handle)) throw new InvalidOperationException("The desktop refused a keyboard session.");
        string session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
        var input = new WaylandInput(bus, session);
        try
        {
            var settings = AppSettings.Load();
            token = Portal.Token("rd");
            var options = new List<KeyValuePair<string, VariantValue>>
            {
                Portal.Option("handle_token", token),
                Portal.Option("types", 1u), // keyboard
                Portal.Option("persist_mode", 2u), // remember until revoked
            };
            if (settings.RemoteDesktopToken is { Length: > 0 } restore) options.Add(Portal.Option("restore_token", restore));
            (code, _) = await Portal.Request(bus, token, Portal.Call(bus, RemoteDesktop, "SelectDevices", "oa{sv}", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                Portal.WriteOptions(ref w, options);
            }), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException("The desktop refused keyboard control.");

            token = Portal.Token("rd");
            (code, results) = await Portal.Request(bus, token, Portal.Call(bus, RemoteDesktop, "Start", "osa{sv}", (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                w.WriteString("");
                Portal.WriteOptions(ref w, [Portal.Option("handle_token", token)]);
            }), PromptTimeout).ConfigureAwait(false);
            if (code != 0)
            {
                settings.RemoteDesktopToken = null;
                TrySave(settings);
                throw new InvalidOperationException("BwPicker wasn't allowed to type. Use Ctrl+U / Ctrl+P to copy instead, or allow it next time.");
            }
            // Each start hands out a fresh token; the previous one is used up.
            settings.RemoteDesktopToken = results.TryGetValue("restore_token", out var next) ? next.GetString() : null;
            TrySave(settings);
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    static void TrySave(AppSettings settings)
    {
        try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Presses (or releases) the key for an X keysym; the desktop picks the keycode and Shift for it.</summary>
    public void Key(long keysym, bool pressed)
    {
        var call = Portal.Call(bus, RemoteDesktop, "NotifyKeyboardKeysym", "oa{sv}iu", (ref MessageWriter w) =>
        {
            w.WriteObjectPath(session);
            Portal.WriteOptions(ref w, Array.Empty<KeyValuePair<string, VariantValue>>());
            w.WriteInt32((int)keysym);
            w.WriteUInt32(pressed ? 1u : 0u);
        });
        Task.Run(() => bus.CallMethodAsync(call)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            using var writer = bus.GetMessageWriter();
            writer.WriteMethodCallHeader(Portal.Service, session, "org.freedesktop.portal.Session", "Close", null);
            bus.TrySendMessage(writer.CreateMessage());
        }
        catch (DBusExceptionBase) { }
    }
}

/// <summary>
/// The keyboard on Wayland: keys go through <see cref="WaylandInput"/>; the destination is checked with AT-SPI
/// (the same app's window must be active and hold focus). If AT-SPI couldn't identify the app when the hotkey was
/// pressed, the picker said so ("unverified") and typing goes to whatever has focus.
/// </summary>
sealed class WaylandKeyboard : IKeyboard, IDisposable
{
    readonly WindowContext target;
    readonly WaylandInput input;
    char pendingHigh;

    /// <summary>Opens the keyboard session first, so a first-time permission prompt comes before any checks or keys.</summary>
    public WaylandKeyboard(WindowContext target)
    {
        this.target = target;
        // Off the UI thread: the DBus library resumes on the caller's context, which is blocked here.
        input = Task.Run(WaylandInput.Open).GetAwaiter().GetResult();
        // A first-time permission dialog takes focus; give the desktop a moment to hand it back to the target.
        for (int i = 0; i < 80 && !target.Unverified && Foreground != target.Handle; i++) Thread.Sleep(25);
    }

    public IntPtr Foreground => target.Unverified ? target.Handle : AtSpi.Active is { } w ? w.Id : IntPtr.Zero;

    /// <summary>The picker waits for modifier keys to be released before typing; Wayland can't report them.</summary>
    public bool ModifiersDown => false;

    /// <summary>Wayland apps can't activate other windows; closing the picker returns focus to the previous one.</summary>
    public bool Focus(IntPtr window) => true;

    public bool FocusInside(IntPtr window) => target.Unverified || (AtSpi.Active is { } w && w.Id == window && AtSpi.FocusedBus == w.Bus);

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);

    /// <summary>
    /// The desktop only types keysyms its keyboard layout has (it can't map others the way X11 allows), so check
    /// every character first. XWayland mirrors the desktop's layout; without it, assume it can be typed.
    /// </summary>
    public unsafe bool CanType(ReadOnlySpan<char> text)
    {
        using var display = X11.Display.Open();
        if (display == null) return true;
        X11.XDisplayKeycodes(display.Handle, out int min, out int max);
        IntPtr map = X11.XGetKeyboardMapping(display.Handle, (byte)min, max - min + 1, out int perCode);
        if (map == IntPtr.Zero) return true;
        var layout = new HashSet<long>();
        try
        {
            var syms = (IntPtr*)map;
            for (int i = 0; i < (max - min + 1) * perCode; i++) if (syms[i] != IntPtr.Zero) layout.Add(syms[i]);
        }
        finally { X11.XFree(map); }
        foreach (var rune in text.EnumerateRunes())
            if (!layout.Contains(Keysym(rune.Value))) return false;
        return true;
    }

    static long Keysym(int codePoint) => codePoint is >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff ? codePoint : 0x01000000 | codePoint;

    public bool Press(KeyStroke key)
    {
        long sym;
        switch (key.Special)
        {
            case SpecialKey.Tab: sym = 0xff09; break;
            case SpecialKey.Enter: sym = 0xff0d; break;
            default:
                char c = key.Character;
                if (char.IsHighSurrogate(c)) { pendingHigh = c; return true; }
                int codePoint = char.IsLowSurrogate(c) && pendingHigh != 0 ? char.ConvertToUtf32(pendingHigh, c) : c;
                pendingHigh = '\0';
                sym = Keysym(codePoint);
                break;
        }
        try
        {
            input.Key(sym, true);
            input.Key(sym, false);
            return true;
        }
        catch (DBusExceptionBase) { return false; }
    }

    public void Dispose() => input.Dispose();
}
