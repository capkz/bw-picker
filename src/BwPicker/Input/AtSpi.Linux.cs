using Tmds.DBus.Protocol;

namespace BwPicker;

/// <summary>
/// Follows which window is active and which app has keyboard focus through the accessibility bus (AT-SPI), the
/// one cross-desktop way to learn this on Wayland, where the compositor doesn't tell apps. GTK, Qt and Firefox
/// report it; Chromium/Electron apps only once accessibility is on, which BwPicker requests at startup.
/// </summary>
static class AtSpi
{
    const string Registry = "org.a11y.atspi.Registry";

    /// <summary>An app's window as AT-SPI sees it: its bus connection and object path identify it.</summary>
    public sealed record Window(string Bus, string Path, uint Pid, string App)
    {
        public int Id { get; init; }
    }

    static readonly object gate = new();
    static readonly Dictionary<(string, string), int> ids = [];
    static DBusConnection? bus;
    static Window? active;
    static string? focusedBus;
    static Task? started;

    /// <summary>The active window outside BwPicker, if the accessibility bus reported one.</summary>
    public static Window? Active { get { lock (gate) return active; } }

    /// <summary>The bus connection of the app whose control has keyboard focus.</summary>
    public static string? FocusedBus { get { lock (gate) return focusedBus; } }

    public static Task Start() { lock (gate) return started ??= Connect(); }

    static async Task Connect()
    {
        try
        {
            if (DBusAddress.Session is not { } sessionAddress) return;
            using var session = new DBusConnection(sessionAddress);
            await session.ConnectAsync().ConfigureAwait(false);
            // Ask toolkits to expose accessibility (Chromium/Electron and Qt check this); it lasts for this session.
            await session.CallMethodAsync(Message(session, "org.a11y.Bus", "/org/a11y/bus", "org.freedesktop.DBus.Properties", "Set", "ssv", (ref MessageWriter w) =>
            {
                w.WriteString("org.a11y.Status");
                w.WriteString("IsEnabled");
                w.WriteVariantBool(true);
            })).ConfigureAwait(false);
            string address = await session.CallMethodAsync(Message(session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", null),
                (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);

            var a11y = new DBusConnection(address);
            await a11y.ConnectAsync().ConfigureAwait(false);
            foreach (string kind in new[] { "window:activate", "window:deactivate", "object:state-changed:focused", "object:state-changed:active" })
            {
                await a11y.CallMethodAsync(Message(a11y, Registry, "/org/a11y/atspi/registry", Registry, "RegisterEvent", "sass", (ref MessageWriter w) =>
                {
                    w.WriteString(kind);
                    w.WriteArray(Array.Empty<string>());
                    w.WriteString("");
                })).ConfigureAwait(false);
            }
            await Watch(a11y, "org.a11y.atspi.Event.Window", "Activate", (sender, path, _detail) => _ = Activated(a11y, sender, path)).ConfigureAwait(false);
            await Watch(a11y, "org.a11y.atspi.Event.Window", "Deactivate", (sender, path, _) => Deactivated(sender, path)).ConfigureAwait(false);
            // GTK 4 reports window activation only as the window's "active" state, not as a window event.
            await Watch(a11y, "org.a11y.atspi.Event.Object", "StateChanged", (sender, path, gained) =>
            {
                if (gained) _ = Activated(a11y, sender, path); else Deactivated(sender, path);
            }, detail: "active").ConfigureAwait(false);
            await Watch(a11y, "org.a11y.atspi.Event.Object", "StateChanged", (sender, path, gained) =>
            {
                if (gained) lock (gate) focusedBus = sender;
            }, detail: "focused").ConfigureAwait(false);
            bus = a11y;
            await FindActive(a11y).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"AT-SPI unavailable: {e.Message}"); // typing is then unverified
        }
    }

    static async Task Watch(DBusConnection a11y, string iface, string member, Action<string, string, bool> handler, string? detail = null)
    {
        var rule = new MatchRule { Type = MessageType.Signal, Interface = iface, Member = member, Arg0 = detail };
        await a11y.AddMatchAsync(rule,
            (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString(); // detail ("focused" for state changes)
                int detail1 = reader.ReadInt32();
                return (Sender: m.SenderAsString ?? "", Path: m.PathAsString ?? "", Detail1: detail1);
            },
            (Notification<(string Sender, string Path, int Detail1)> n) => { if (n.HasValue) handler(n.Value.Sender, n.Value.Path, n.Value.Detail1 == 1); },
            false, ObserverFlags.None, null).ConfigureAwait(false);
    }

    static void Deactivated(string sender, string path)
    {
        lock (gate) if (active?.Bus == sender && active.Path == path) active = null;
    }

    static async Task Activated(DBusConnection a11y, string sender, string path)
    {
        try
        {
            uint pid = await a11y.CallMethodAsync(Message(a11y, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                "GetConnectionUnixProcessID", "s", (ref MessageWriter w) => w.WriteString(sender)), (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
            if (pid == Environment.ProcessId) return; // BwPicker's own windows
            string app = await Name(a11y, sender, "/org/a11y/atspi/accessible/root").ConfigureAwait(false) ?? "";
            lock (gate)
            {
                if (!ids.TryGetValue((sender, path), out int id)) ids[(sender, path)] = id = ids.Count + 1;
                active = new Window(sender, path, pid, app) { Id = id };
                focusedBus ??= sender;
            }
        }
        catch (DBusExceptionBase) { }
    }

    /// <summary>At startup nothing has been activated yet: look for the window whose state says it is active.</summary>
    static async Task FindActive(DBusConnection a11y)
    {
        foreach (var (appBus, appPath) in await Children(a11y, Registry, "/org/a11y/atspi/accessible/root").ConfigureAwait(false))
        {
            foreach (var (windowBus, windowPath) in await Children(a11y, appBus, appPath).ConfigureAwait(false))
            {
                try
                {
                    uint[] states = await a11y.CallMethodAsync(Message(a11y, windowBus, windowPath, "org.a11y.atspi.Accessible", "GetState", null),
                        (Message m, object? _) => m.GetBodyReader().ReadArrayOfUInt32(), null).ConfigureAwait(false);
                    if (states.Length > 0 && (states[0] & (1u << 1)) != 0) { await Activated(a11y, windowBus, windowPath).ConfigureAwait(false); return; } // STATE_ACTIVE
                }
                catch (DBusExceptionBase) { }
            }
        }
    }

    static async Task<List<(string, string)>> Children(DBusConnection a11y, string service, string path)
    {
        try
        {
            return await a11y.CallMethodAsync(Message(a11y, service, path, "org.a11y.atspi.Accessible", "GetChildren", null), (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                var list = new List<(string, string)>();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    list.Add((reader.ReadString(), reader.ReadObjectPathAsString()));
                }
                return list;
            }, null).ConfigureAwait(false);
        }
        catch (DBusExceptionBase) { return []; }
    }

    /// <summary>The accessible name (for a window, its title); null if the object is gone.</summary>
    static async Task<string?> Name(DBusConnection a11y, string service, string path)
    {
        try
        {
            var value = await a11y.CallMethodAsync(Message(a11y, service, path, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) =>
            {
                w.WriteString("org.a11y.atspi.Accessible");
                w.WriteString("Name");
            }), (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            return value.GetString();
        }
        catch (DBusExceptionBase) { return null; }
    }

    /// <summary>The window's current title, read live; null if it no longer exists.</summary>
    public static string? Title(Window window)
    {
        if (bus is not { } a11y) return null;
        try { return Task.Run(() => Name(a11y, window.Bus, window.Path)).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return null; }
    }

    static MessageBuffer Message(DBusConnection connection, string service, string path, string iface, string member, string? signature,
        BodyWriter? body = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(service, path, iface, member, signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }
}
