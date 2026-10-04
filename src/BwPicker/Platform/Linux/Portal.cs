using Tmds.DBus.Protocol;

namespace BwPicker;

/// <summary>
/// xdg-desktop-portal on the session bus: how Wayland apps ask the desktop for things they can't do themselves
/// (a global shortcut, sending key events). Requests answer asynchronously through a Request object's Response
/// signal. All calls use ConfigureAwait(false): typing waits on them synchronously from the UI thread.
/// </summary>
/// <summary>Writes a message body. MessageWriter is a struct, so it must be passed by reference.</summary>
delegate void BodyWriter(ref MessageWriter writer);

static class Portal
{
    public const string Service = "org.freedesktop.portal.Desktop", ObjectPath = "/org/freedesktop/portal/desktop";
    /// <summary>The app ID the portal knows BwPicker by; matches bwpicker.desktop, which install-linux.sh installs.</summary>
    public const string AppId = "bwpicker";

    static readonly SemaphoreSlim gate = new(1, 1);
    static DBusConnection? connection;
    static int counter;

    /// <summary>The shared portal connection, registered as <see cref="AppId"/>; null without a session bus.</summary>
    public static async Task<DBusConnection?> Connect()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (connection != null) return connection;
            if (DBusAddress.Session is not { } address) return null;
            var bus = new DBusConnection(address);
            try { await bus.ConnectAsync().ConfigureAwait(false); }
            catch (DBusExceptionBase) { bus.Dispose(); return null; }
            // Apps outside Flatpak/Snap tell the portal who they are first, so permissions and shortcuts are
            // remembered for BwPicker. Older portals lack this; they then identify the app by its systemd scope.
            try
            {
                await bus.CallMethodAsync(Call(bus, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter w) =>
                {
                    w.WriteString(AppId);
                    Portal.WriteOptions(ref w, [Portal.Option("bwpicker", true)]);
                })).ConfigureAwait(false);
            }
            catch (DBusExceptionBase e) { System.Diagnostics.Trace.WriteLine($"Portal registry: {e.Message}"); }
            return connection = bus;
        }
        finally { gate.Release(); }
    }

    /// <summary>The interface's version property, or 0 if the portal doesn't offer it.</summary>
    public static async Task<uint> Version(DBusConnection bus, string iface)
    {
        try
        {
            var reply = await bus.CallMethodAsync(Call(bus, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) =>
            {
                w.WriteString(iface);
                w.WriteString("version");
            }), (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            return reply.GetUInt32();
        }
        catch (DBusExceptionBase e)
        {
            System.Diagnostics.Trace.WriteLine($"Portal {iface} version: {e.Message}");
            return 0;
        }
    }

    public static MessageBuffer Call(DBusConnection bus, string iface, string member, string? signature, BodyWriter? body = null)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, iface, member, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    /// <summary>A unique handle_token for a request or session.</summary>
    public static string Token(string prefix) => $"bwpicker_{prefix}{Interlocked.Increment(ref counter)}";

    /// <summary>
    /// Makes a portal request and waits for its Response: (0 = success, 1 = cancelled by the user, 2 = other), with
    /// the results. The Response subscription is set up before the call, at the documented request path, so a quick
    /// answer can't be missed.
    /// </summary>
    public static async Task<(uint Code, Dictionary<string, VariantValue> Results)> Request(DBusConnection bus, string token,
        MessageBuffer call, TimeSpan timeout)
    {
        string sender = bus.UniqueName!.TrimStart(':').Replace('.', '_');
        string path = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var response = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await bus.WatchSignalAsync(Service, path, "org.freedesktop.portal.Request", "Response",
            (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            (Notification<(uint, Dictionary<string, VariantValue>)> n) => { if (n.HasValue) response.TrySetResult(n.Value); },
            ObserverFlags.None, false, null).ConfigureAwait(false);
        await bus.CallMethodAsync(call).ConfigureAwait(false);
        return await response.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    public static KeyValuePair<string, VariantValue> Option(string key, VariantValue value) => new(key, value);

    /// <summary>Writes an a{sv} options dictionary entry by entry.</summary>
    public static void WriteOptions(ref MessageWriter writer, IEnumerable<KeyValuePair<string, VariantValue>> options)
    {
        var start = writer.WriteDictionaryStart();
        foreach (var (key, value) in options)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariant(value);
        }
        writer.WriteDictionaryEnd(start);
    }
}
