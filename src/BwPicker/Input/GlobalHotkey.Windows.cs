using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// System-wide Ctrl+Alt+B via RegisterHotKey on a dedicated thread with its own message loop, independent of the
/// UI framework. <see cref="Pressed"/> is raised on that thread; marshal to the UI thread before use.
/// </summary>
sealed class GlobalHotkey : IDisposable
{
    const int WM_QUIT = 0x0012;
    readonly Thread thread;
    readonly TaskCompletionSource<bool> registered = new();
    uint threadId;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        const uint key = 0x42; // B
        thread = new Thread(() => Loop(Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, key)) { IsBackground = true, Name = "BwPicker hotkey" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>False if another app already owns the combination.</summary>
    public bool Registered => registered.Task.GetAwaiter().GetResult();

    /// <summary>Why the hotkey isn't available, if the generic "already in use" doesn't apply.</summary>
    public string? Problem => null;

    /// <summary>Raised if the hotkey stops being available later (only used on Linux).</summary>
    public event Action<string>? Unavailable { add { } remove { } }

    void Loop(uint modifiers, uint key)
    {
        threadId = GetCurrentThreadId();
        bool ok = Native.RegisterHotKey(IntPtr.Zero, 1, modifiers, key);
        registered.TrySetResult(ok);
        if (!ok) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == Native.WM_HOTKEY) Pressed?.Invoke();
        }
        finally { Native.UnregisterHotKey(IntPtr.Zero, 1); }
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
