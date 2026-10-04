using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>Encrypts buffers (multiples of 16 bytes) in place with a key bound to this process (CryptProtectMemory).</summary>
static class MemoryProtection
{
    public static bool Protect(byte[] buffer) => CryptProtectMemory(buffer, (uint)buffer.Length, 0);
    public static bool Unprotect(byte[] buffer) => CryptUnprotectMemory(buffer, (uint)buffer.Length, 0);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectMemory([In, Out] byte[] data, uint length, uint flags);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectMemory([In, Out] byte[] data, uint length, uint flags);
}
