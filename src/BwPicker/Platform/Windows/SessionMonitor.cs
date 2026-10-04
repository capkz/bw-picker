using Microsoft.Win32;

namespace BwPicker;

/// <summary>Raises events when the Windows session locks or unlocks and when the PC sleeps or wakes.</summary>
sealed class SessionMonitor : IDisposable
{
    public event Action? Locked, Unlocked, Suspending, Resumed;

    public SessionMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) Locked?.Invoke();
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or
            SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) Unlocked?.Invoke();
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Suspending?.Invoke();
        else if (e.Mode == PowerModes.Resume) Resumed?.Invoke();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
