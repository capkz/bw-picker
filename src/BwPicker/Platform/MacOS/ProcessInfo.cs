namespace BwPicker;

/// <summary>Facts about the process behind a window, from proc_pidinfo / proc_pidpath.</summary>
readonly record struct ProcessInfo(long StartedAt, string ImagePath, bool? Elevated)
{
    public static bool CurrentIsElevated { get; } = Environment.IsPrivilegedProcess;

    public static ProcessInfo? Read(uint processId)
    {
        if (!MacNative.ProcessBsdInfo((int)processId, out long started, out uint uid) || started == 0) return null;
        return new ProcessInfo(started, MacNative.ProcessPath((int)processId), uid == 0);
    }
}
