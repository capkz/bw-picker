using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace BwPicker;

/// <summary>
/// Facts about the process behind a window, read with PROCESS_QUERY_LIMITED_INFORMATION, which Windows
/// grants even for processes running as administrator (unlike Process.StartTime / MainModule).
/// </summary>
readonly record struct ProcessInfo(long StartedAt, string ImagePath, bool? Elevated)
{
    public static bool CurrentIsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static ProcessInfo? Read(uint processId)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(process, out long created, out _, out _, out _)) return null;
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            string image = QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : "";
            return new ProcessInfo(created, image, ReadElevation(process));
        }
        finally { CloseHandle(process); }
    }

    /// <summary>True/false when readable; null when Windows denies the token, which means it outranks us.</summary>
    static bool? ReadElevation(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return null;
        try { return GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out _) ? elevated != 0 : null; }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
