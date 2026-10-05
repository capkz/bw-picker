using System.Diagnostics;
using Avalonia.Controls;

namespace BwPicker;

/// <summary>
/// The menu-bar icon (Avalonia's NSStatusItem) and notifications. Native notification APIs need a signed app, so
/// notifications go through AppleScript's "display notification", which works for unsigned apps too; clicking them
/// can't call back, so click actions aren't available on macOS.
/// </summary>
sealed class NativeTray : IDisposable
{
    public sealed record MenuItem(string Text, Action? Action, bool IsDefault = false)
    {
        public static readonly MenuItem Separator = new("", null);
    }

    readonly TrayIcon tray;
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
        if (Avalonia.Application.Current is { } app) TrayIcon.SetIcons(app, [tray]);
    }

    public void Notify(string title, string message, Notice kind, Action? onClick = null)
    {
        if (disposed) return;
        try
        {
            // Pass the text as arguments, never spliced into the script.
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList =
                {
                    "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
                    title, message,
                },
            };
            Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tray.IsVisible = false;
        tray.Dispose();
    }
}
