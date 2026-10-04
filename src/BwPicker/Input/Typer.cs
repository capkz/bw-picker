namespace BwPicker;

/// <summary>A key to press: a character, or Tab / Enter.</summary>
readonly record struct KeyStroke(char Character, SpecialKey Special = SpecialKey.None)
{
    public static readonly KeyStroke Tab = new('\0', SpecialKey.Tab), Enter = new('\0', SpecialKey.Enter);
}

enum SpecialKey { None, Tab, Enter }

interface IKeyboard
{
    IntPtr Foreground { get; }
    bool ModifiersDown { get; }
    bool Focus(IntPtr window);
    /// <summary>True while keyboard focus is the window itself or any control inside it.</summary>
    bool FocusInside(IntPtr window);
    /// <summary>Presses and releases one key; false if the system refused the input.</summary>
    bool Press(KeyStroke key);
    void Wait(int milliseconds);
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
        void SendKey(KeyStroke key)
        {
            Guard();
            if (!keyboard.Press(key)) throw new InvalidOperationException("The system blocked credential typing.");
        }
        void Text(ReadOnlySpan<char> text)
        {
            foreach (char c in text) { SendKey(new KeyStroke(c)); keyboard.Wait(4); }
        }
        Guard();
        bool typeUsername = fields != TypeFields.PasswordOnly && !string.IsNullOrEmpty(credential.Username);
        if (typeUsername) Text(credential.Username);
        if (fields == TypeFields.Both && typeUsername)
        {
            SendKey(KeyStroke.Tab);
            keyboard.Wait(30);
        }
        if (fields != TypeFields.UsernameOnly && credential.Password is { } password) Text(password.Characters);
        if (submit) SendKey(KeyStroke.Enter);
    }
    static void Validate(ReadOnlySpan<char> text)
    {
        if (text.Length > 4096) throw new InvalidOperationException("This credential is too long for safe automatic typing.");
        foreach (char c in text)
            if (char.IsControl(c)) throw new InvalidOperationException("This credential contains control characters and cannot be typed safely.");
    }
}

static partial class Typer
{
    public static void TypeCredentials(WindowContext target, CredentialLease credentials, bool submit, TypeFields fields)
    {
        var keyboard = CreateKeyboard(target);
        try { new InputTyper(keyboard).Type(target.Handle, credentials, submit, target.HasOriginalIdentity, target.WindowStillMatches, fields); }
        finally { (keyboard as IDisposable)?.Dispose(); }
    }

    private static partial IKeyboard CreateKeyboard(WindowContext target);
}
