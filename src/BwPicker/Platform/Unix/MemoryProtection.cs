using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BwPicker;

/// <summary>
/// Encrypts buffers (multiples of 16 bytes) in place with a random key that exists only in this process, the Linux and
/// macOS counterpart of CryptProtectMemory: secrets never sit in memory as plaintext between uses. The key lives in a
/// pinned buffer that is locked into RAM (never swapped) and excluded from core dumps where the kernel allows it.
/// </summary>
static class MemoryProtection
{
    static readonly byte[] key = CreateKey();
    static readonly Aes aes = CreateAes();

    static byte[] CreateKey()
    {
        var bytes = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(bytes);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                // Best effort: these can fail under tight RLIMIT_MEMLOCK; the key is still process-only.
                _ = mlock((IntPtr)p, (nuint)bytes.Length);
#if LINUX
                long page = Environment.SystemPageSize;
                long start = (long)p & ~(page - 1);
                _ = madvise((IntPtr)start, (nuint)page, 16 /* MADV_DONTDUMP */);
#endif
            }
        }
        return bytes;
    }

    static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    public static bool Protect(byte[] buffer) => Transform(buffer, encrypt: true);
    public static bool Unprotect(byte[] buffer) => Transform(buffer, encrypt: false);

    static bool Transform(byte[] buffer, bool encrypt)
    {
        if (buffer.Length % 16 != 0) return false;
        lock (aes)
        {
            // ECB keeps the length and works in place (copies of a buffer must stay decryptable without extra state),
            // like CryptProtectMemory. The goal is the same: no plaintext at rest, with a key only this process has.
            if (encrypt) aes.EncryptEcb(buffer, buffer, PaddingMode.None);
            else aes.DecryptEcb(buffer, buffer, PaddingMode.None);
        }
        return true;
    }

    [DllImport("libc", SetLastError = true)] static extern int mlock(IntPtr address, nuint length);
#if LINUX
    [DllImport("libc", SetLastError = true)] static extern int madvise(IntPtr address, nuint length, int advice);
#endif
}
