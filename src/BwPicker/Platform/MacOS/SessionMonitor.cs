namespace BwPicker;

/// <summary>
/// Screen lock and sleep on macOS, polled every two seconds so no Objective-C observer is needed: the session
/// dictionary says whether the screen is locked, and sleep shows up as wall-clock time passing while the process's
/// monotonic clock (which stops during sleep) didn't.
/// </summary>
sealed class SessionMonitor : IDisposable
{
    readonly System.Threading.Timer timer;
    bool locked;
    long lastTicks = MacNative.UptimeMilliseconds();
    DateTime lastWall = DateTime.UtcNow;

    public event Action? Locked, Unlocked, Suspending, Resumed;

    public SessionMonitor() => timer = new System.Threading.Timer(_ => Poll(), null, 2000, 2000);

    void Poll()
    {
        long ticks = MacNative.UptimeMilliseconds();
        DateTime wall = DateTime.UtcNow;
        double slept = (wall - lastWall).TotalMilliseconds - (ticks - lastTicks);
        lastTicks = ticks;
        lastWall = wall;
        if (slept > 15_000)
        {
            // Woke from sleep: treat it like a suspend that just ended, so the vault is locked before anything else.
            Suspending?.Invoke();
            Resumed?.Invoke();
        }

        bool now = MacNative.ScreenLocked();
        if (now == locked) return;
        locked = now;
        (now ? Locked : Unlocked)?.Invoke();
    }

    public void Dispose() => timer.Dispose();
}
