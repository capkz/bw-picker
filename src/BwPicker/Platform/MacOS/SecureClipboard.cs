namespace BwPicker;

/// <summary>
/// Copies to the general pasteboard, marked with org.nspasteboard.ConcealedType and TransientType, the convention
/// clipboard managers and Universal Clipboard sync use to leave passwords alone. BwPicker remembers the pasteboard's
/// change count, so clearing only removes its own copy, not something the user copied later.
/// </summary>
static class SecureClipboard
{
    static readonly object gate = new();
    static long ownedChange = -1;
    static System.Threading.Timer? clearTimer;

    public static void Set(ReadOnlySpan<char> text, Func<bool> valid)
    {
        lock (gate)
        {
            if (!valid()) throw new InvalidOperationException("Vault was locked; nothing was copied.");
            IntPtr value = MacNative.NSString(text);
            try
            {
                ownedChange = MacNative.WithPool(() =>
                {
                    IntPtr board = MacNative.Send(MacNative.Class("NSPasteboard"), "generalPasteboard");
                    MacNative.Send(board, "clearContents");
                    Write(board, "public.utf8-plain-text", value);
                    IntPtr empty = MacNative.NSString("");
                    try
                    {
                        Write(board, "org.nspasteboard.ConcealedType", empty);
                        Write(board, "org.nspasteboard.TransientType", empty);
                    }
                    finally { MacNative.Release(empty); }
                    return MacNative.SendLong(board, "changeCount");
                });
            }
            finally { MacNative.Release(value); }
            if (!valid()) { ClearOwned(); throw new InvalidOperationException("Vault was locked; the copied credential was cleared."); }
            clearTimer?.Dispose();
            clearTimer = new System.Threading.Timer(_ => ClearOwned(), null, 30_000, Timeout.Infinite);
        }
    }

    static void Write(IntPtr board, string type, IntPtr value)
    {
        IntPtr typeName = MacNative.NSString(type);
        try { MacNative.Send(board, "setString:forType:", value, typeName); }
        finally { MacNative.Release(typeName); }
    }

    public static void ClearOwned()
    {
        lock (gate)
        {
            clearTimer?.Dispose();
            clearTimer = null;
            if (ownedChange < 0) return;
            long owned = ownedChange;
            ownedChange = -1;
            MacNative.WithPool(() =>
            {
                IntPtr board = MacNative.Send(MacNative.Class("NSPasteboard"), "generalPasteboard");
                if (MacNative.SendLong(board, "changeCount") == owned) MacNative.Send(board, "clearContents");
                return 0;
            });
        }
    }
}
