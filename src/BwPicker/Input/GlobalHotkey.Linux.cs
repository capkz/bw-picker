using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// Ctrl+Alt+B. On X11 it is grabbed on the root window from a dedicated thread with its own display connection.
/// Wayland doesn't let apps grab keys, so there (and on X11 too) running `BwPicker --pick` does the same through a
/// socket in the user's runtime folder; the desktop's own keyboard-shortcut settings can bind that command.
/// <see cref="Pressed"/> is raised on a background thread; marshal to the UI thread before use.
/// </summary>
sealed class GlobalHotkey : IDisposable
{
    const long KeysymB = 0x62;
    const uint Modifiers = X11.ControlMask | X11.Mod1Mask;

    readonly Thread? thread;
    readonly TaskCompletionSource<bool> registered = new();
    readonly Socket? listener;
    volatile bool running = true;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        listener = Listen();
        if (X11.Available)
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "BwPicker hotkey" };
            thread.Start();
        }
        else registered.TrySetResult(false);
    }

    /// <summary>False if the key couldn't be grabbed; <see cref="Problem"/> says why when it isn't simply taken.</summary>
    public bool Registered => registered.Task.GetAwaiter().GetResult();

    public string? Problem => X11.Available ? null :
        $"On Wayland, add a keyboard shortcut in your desktop settings (e.g. Ctrl+Alt+B) that runs: {Environment.ProcessPath} --pick";

    /// <summary>The socket `--pick` connects to; only this user can reach their runtime folder.</summary>
    public static string? SocketPath =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? Path.Combine(runtime, "bwpicker.sock") : null;

    /// <summary>Asks the running BwPicker to open the picker; false if none is running.</summary>
    public static bool SendPick()
    {
        if (SocketPath is not { } path || !File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send("pick\n"u8);
            return true;
        }
        catch (SocketException) { return false; }
    }

    Socket? Listen()
    {
        if (SocketPath is not { } path) return null;
        try
        {
            File.Delete(path); // a stale socket from a crashed instance; the single-instance lock is already held
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(4);
            _ = Accept(socket);
            return socket;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException) { return null; }
    }

    async Task Accept(Socket socket)
    {
        var buffer = new byte[16];
        while (running)
        {
            try
            {
                using var client = await socket.AcceptAsync();
                int read = await client.ReceiveAsync(buffer).WaitAsync(TimeSpan.FromSeconds(2));
                if (buffer.AsSpan(0, read).StartsWith("pick"u8)) Pressed?.Invoke();
            }
            catch (Exception e) when (e is SocketException or TimeoutException or ObjectDisposedException)
            {
                if (!running) return;
            }
        }
    }

    void Loop()
    {
        using var display = X11.Display.Open();
        if (display == null) { registered.TrySetResult(false); return; }
        int keycode = X11.XKeysymToKeycode(display.Handle, (IntPtr)KeysymB);
        bool ok = keycode != 0 && X11.GrabKey(display, keycode, Modifiers);
        registered.TrySetResult(ok);
        if (!ok) return;
        // Holding the keys sends repeated presses without releases, so one hold opens the picker once.
        X11.XkbSetDetectableAutoRepeat(display.Handle, true, out _);
        try
        {
            var poll = new PollFd { fd = X11.XConnectionNumber(display.Handle), events = 1 /* POLLIN */ };
            bool down = false;
            while (running)
            {
                while (X11.XPending(display.Handle) > 0)
                {
                    X11.XNextEvent(display.Handle, out var e);
                    if (e.type == X11.KeyPress && !down) { down = true; Pressed?.Invoke(); }
                    else if (e.type == X11.KeyRelease) down = false;
                }
                poll.revents = 0;
                _ = Poll(ref poll, 1, 250);
            }
        }
        finally { X11.UngrabKey(display, keycode, Modifiers); }
    }

    public void Dispose()
    {
        running = false;
        listener?.Dispose();
        if (SocketPath is { } path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        thread?.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd { public int fd; public short events, revents; }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    static extern int Poll(ref PollFd fds, nuint count, int timeout);
}
