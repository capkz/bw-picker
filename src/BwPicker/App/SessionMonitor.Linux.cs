using Tmds.DBus.Protocol;

namespace BwPicker;

/// <summary>
/// Raises events when the screen locks or unlocks and when the computer sleeps or wakes, from systemd-logind
/// (Lock/Unlock of this session, PrepareForSleep) and the desktop's screensaver (ActiveChanged). Desktops differ
/// in which of these they send, so all are watched.
/// </summary>
sealed class SessionMonitor : IDisposable
{
    const string Login = "org.freedesktop.login1", LoginPath = "/org/freedesktop/login1";

    readonly List<IDisposable> watches = [];
    readonly List<DBusConnection> connections = [];
    bool disposed;

    public event Action? Locked, Unlocked, Suspending, Resumed;

    public SessionMonitor() => _ = Start();

    async Task Start()
    {
        if (await Connect(DBusAddress.System) is { } system)
        {
            await Watch(system, Login, LoginPath, $"{Login}.Manager", "PrepareForSleep",
                sleeping => (sleeping ? Suspending : Resumed)?.Invoke());
            try
            {
                string session = await system.CallMethodAsync(SessionByPid(system),
                    (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
                await WatchEmpty(system, session, $"{Login}.Session", "Lock", () => Locked?.Invoke());
                await WatchEmpty(system, session, $"{Login}.Session", "Unlock", () => Unlocked?.Invoke());
            }
            catch (DBusExceptionBase) { } // not started inside a logind session
        }
        if (await Connect(DBusAddress.Session) is { } user)
        {
            foreach (var (path, name) in new[] { ("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver"), ("/org/gnome/ScreenSaver", "org.gnome.ScreenSaver") })
                await Watch(user, null, path, name, "ActiveChanged", active => (active ? Locked : Unlocked)?.Invoke());
        }
    }

    static MessageBuffer SessionByPid(DBusConnection bus)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Login, LoginPath, $"{Login}.Manager", "GetSessionByPID", "u");
        writer.WriteUInt32((uint)Environment.ProcessId);
        return writer.CreateMessage();
    }

    async Task<DBusConnection?> Connect(string? address)
    {
        if (address == null || disposed) return null;
        var bus = new DBusConnection(address);
        try { await bus.ConnectAsync(); }
        catch (DBusExceptionBase) { bus.Dispose(); return null; }
        lock (connections)
        {
            if (disposed) { bus.Dispose(); return null; }
            connections.Add(bus);
        }
        return bus;
    }

    async Task Watch(DBusConnection bus, string? sender, string path, string iface, string signal, Action<bool> handler)
    {
        try
        {
            var watch = await bus.WatchSignalAsync(sender!, path, iface, signal,
                (Message m, object? _) => m.GetBodyReader().ReadBool(),
                (Notification<bool> n) => { if (n.HasValue) handler(n.Value); }, ObserverFlags.None, false, null);
            lock (connections) watches.Add(watch);
        }
        catch (DBusExceptionBase) { }
    }

    async Task WatchEmpty(DBusConnection bus, string path, string iface, string signal, Action handler)
    {
        try
        {
            var watch = await bus.WatchSignalAsync(Login, path, iface, signal,
                (Notification n) => { if (n.Type == NotificationType.Value) handler(); }, ObserverFlags.None, false, null);
            lock (connections) watches.Add(watch);
        }
        catch (DBusExceptionBase) { }
    }

    public void Dispose()
    {
        lock (connections)
        {
            disposed = true;
            foreach (var watch in watches) watch.Dispose();
            foreach (var bus in connections) bus.Dispose();
            watches.Clear();
            connections.Clear();
        }
    }
}
