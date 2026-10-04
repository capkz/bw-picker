using Avalonia.Controls;
using Tmds.DBus.Protocol;

namespace BwPicker;

/// <summary>
/// The tray icon (Avalonia's StatusNotifierItem, shown by most Linux panels) and desktop notifications through the
/// freedesktop Notifications service, so they look like every other notification and are kept by the desktop.
/// Create and use on the UI thread.
/// </summary>
sealed class NativeTray : IDisposable
{
    public sealed record MenuItem(string Text, Action? Action, bool IsDefault = false)
    {
        public static readonly MenuItem Separator = new("", null);
    }

    const string Service = "org.freedesktop.Notifications", ObjectPath = "/org/freedesktop/Notifications";

    readonly TrayIcon tray;
    readonly Dictionary<uint, Action> clickActions = [];
    readonly object gate = new();
    Task<DBusConnection?>? connection;
    IDisposable? actionWatch;
    bool disposed;

    public event Action? Clicked;

    public NativeTray(string tooltip, IReadOnlyList<MenuItem> menu)
    {
        var nativeMenu = new NativeMenu();
        foreach (var item in menu)
        {
            if (ReferenceEquals(item, MenuItem.Separator)) { nativeMenu.Items.Add(new NativeMenuItemSeparator()); continue; }
            var entry = new NativeMenuItem(item.Text);
            entry.Click += (_, _) => item.Action?.Invoke();
            nativeMenu.Items.Add(entry);
        }
        tray = new TrayIcon { Icon = Ui.AppIcon, ToolTipText = tooltip, Menu = nativeMenu, IsVisible = true };
        tray.Clicked += (_, _) => Clicked?.Invoke();
        // Tray icons are owned by the application; register it so the platform keeps it alive and shown.
        if (Avalonia.Application.Current is { } app) TrayIcon.SetIcons(app, [tray]);
    }

    /// <summary>Shows a desktop notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Notify(string title, string message, Notice kind, Action? onClick = null)
    {
        if (disposed) return;
        _ = Send(title, message, kind, onClick);
    }

    async Task Send(string title, string message, Notice kind, Action? onClick)
    {
        try
        {
            if (await Connect() is not { } bus) return;
            var call = CreateNotify(bus, title, message, kind, clickable: onClick != null);
            uint id = await bus.CallMethodAsync(call, (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);
            if (onClick != null) lock (gate) clickActions[id] = onClick;
        }
        catch (DBusExceptionBase) { } // no notification service: nothing to show it with
    }

    static MessageBuffer CreateNotify(DBusConnection bus, string title, string message, Notice kind, bool clickable)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i");
        writer.WriteString("BwPicker");
        writer.WriteUInt32(0); // replaces no earlier notification
        writer.WriteString(kind switch { Notice.Error => "dialog-error", Notice.Warning => "dialog-warning", _ => AppIconPath ?? "dialog-password" });
        writer.WriteString(title);
        writer.WriteString(message);
        writer.WriteArray(clickable ? ["default", "Open"] : Array.Empty<string>());
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("urgency");
        writer.WriteVariantByte(kind == Notice.Error ? (byte)2 : (byte)1);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString("bwpicker");
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(-1); // the server's default timeout
        return writer.CreateMessage();
    }

    /// <summary>The icon install-linux.sh puts next to the app, if present.</summary>
    static readonly string? AppIconPath = Path.Combine(AppContext.BaseDirectory, "bw-picker.png") is var icon && File.Exists(icon) ? icon : null;

    Task<DBusConnection?> Connect()
    {
        lock (gate) return connection ??= Open();
    }

    async Task<DBusConnection?> Open()
    {
        if (DBusAddress.Session is not { } address) return null;
        var bus = new DBusConnection(address);
        try
        {
            await bus.ConnectAsync();
            actionWatch = await bus.WatchSignalAsync(Service, ObjectPath, Service, "ActionInvoked",
                (Message m, object? _) => { var r = m.GetBodyReader(); return (Id: r.ReadUInt32(), Action: r.ReadString()); },
                (Notification<(uint Id, string Action)> signal) =>
                {
                    if (!signal.HasValue) return;
                    Action? click;
                    lock (gate) { clickActions.Remove(signal.Value.Id, out click); }
                    if (click != null) Avalonia.Threading.Dispatcher.UIThread.Post(click);
                }, ObserverFlags.None, false, null);
            return bus;
        }
        catch (DBusExceptionBase) { bus.Dispose(); return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tray.IsVisible = false;
        tray.Dispose();
        actionWatch?.Dispose();
        if (connection is { IsCompletedSuccessfully: true, Result: { } bus }) bus.Dispose();
    }
}
