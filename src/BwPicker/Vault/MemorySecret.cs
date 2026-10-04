using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BwPicker;

/// <summary>Process-bound encrypted storage, exposed only in disposable, wipeable leases.</summary>
sealed class MemorySecret : IDisposable
{
    readonly object gate = new();
    readonly byte[] buffer;
    readonly int length;
    bool disposed;

    public MemorySecret(ReadOnlySpan<char> value)
    {
        length = Encoding.UTF8.GetByteCount(value);
        buffer = GC.AllocateUninitializedArray<byte>(Math.Max(16, (length + 15) / 16 * 16), pinned: true);
        buffer.AsSpan().Clear();
        Encoding.UTF8.GetBytes(value, buffer);
        Protect();
    }

    public MemorySecret(ReadOnlySpan<byte> utf8)
    {
        length = utf8.Length;
        buffer = GC.AllocateUninitializedArray<byte>(Math.Max(16, (length + 15) / 16 * 16), pinned: true);
        buffer.AsSpan().Clear();
        utf8.CopyTo(buffer);
        Protect();
    }

    void Protect()
    {
        if (!CryptProtectMemory(buffer, (uint)buffer.Length, 0))
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidOperationException("Could not protect the credential cache.", new Win32Exception());
        }
    }

    public SecretLease Reveal()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var copy = GC.AllocateUninitializedArray<byte>(buffer.Length, pinned: true);
            buffer.CopyTo(copy, 0);
            try
            {
                if (!CryptUnprotectMemory(copy, (uint)copy.Length, 0))
                    throw new InvalidOperationException("Could not read the credential cache.", new Win32Exception());
                return new SecretLease(copy.AsSpan(0, length));
            }
            finally { CryptographicOperations.ZeroMemory(copy); }
        }
    }

    public delegate T BytesFunc<T>(ReadOnlySpan<byte> bytes);

    /// <summary>Runs <paramref name="use"/> on the raw bytes (e.g. a binary key) in a buffer wiped afterwards.</summary>
    public T UseBytes<T>(BytesFunc<T> use)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var copy = GC.AllocateUninitializedArray<byte>(buffer.Length, pinned: true);
            buffer.CopyTo(copy, 0);
            try
            {
                if (!CryptUnprotectMemory(copy, (uint)copy.Length, 0))
                    throw new InvalidOperationException("Could not read the credential cache.", new Win32Exception());
                return use(copy.AsSpan(0, length));
            }
            finally { CryptographicOperations.ZeroMemory(copy); }
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; CryptographicOperations.ZeroMemory(buffer); }
    }
    public override string ToString() => "[protected]";

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectMemory([In, Out] byte[] data, uint length, uint flags);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectMemory([In, Out] byte[] data, uint length, uint flags);
}

sealed class SecretLease : IDisposable
{
    readonly char[] characters;
    bool disposed;
    public SecretLease(ReadOnlySpan<byte> utf8)
    {
        characters = GC.AllocateUninitializedArray<char>(Encoding.UTF8.GetCharCount(utf8), pinned: true);
        Encoding.UTF8.GetChars(utf8, characters);
    }
    public ReadOnlySpan<char> Characters
    {
        get { ObjectDisposedException.ThrowIf(disposed, this); return characters; }
    }
    public void Dispose()
    {
        disposed = true;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
    }
    public override string ToString() => "[redacted]";
}

/// <summary>Owns CLI output without converting an entire vault to an immutable string.</summary>
sealed class SensitiveBytes : IDisposable
{
    byte[] buffer;
    public int Length { get; private set; }
    public ReadOnlyMemory<byte> Memory => buffer.AsMemory(0, Length);
    public SensitiveBytes(ReadOnlySpan<byte> value)
    {
        buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, value.Length));
        value.CopyTo(buffer);
        Length = value.Length;
    }
    SensitiveBytes() { buffer = ArrayPool<byte>.Shared.Rent(16 * 1024); }
    public static async Task<SensitiveBytes> Read(Stream stream, int limit, CancellationToken cancellation)
    {
        var result = new SensitiveBytes();
        try
        {
            while (true)
            {
                if (result.Length == result.buffer.Length)
                {
                    if (result.Length >= limit) throw new InvalidOperationException("Bitwarden output exceeded the size limit.");
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Min(limit, result.buffer.Length * 2));
                    result.buffer.AsSpan(0, result.Length).CopyTo(grown);
                    CryptographicOperations.ZeroMemory(result.buffer);
                    ArrayPool<byte>.Shared.Return(result.buffer);
                    result.buffer = grown;
                }
                int read = await stream.ReadAsync(result.buffer.AsMemory(result.Length,
                    Math.Min(result.buffer.Length - result.Length, limit - result.Length)), cancellation);
                if (read == 0) return result;
                result.Length += read;
                if (result.Length >= limit) throw new InvalidOperationException("Bitwarden output exceeded the size limit.");
            }
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose()
    {
        var previous = buffer;
        buffer = [];
        Length = 0;
        if (previous.Length == 0) return;
        CryptographicOperations.ZeroMemory(previous);
        ArrayPool<byte>.Shared.Return(previous);
    }
}

sealed class CredentialLease(string? username, SecretLease? password, Func<bool> valid) : IDisposable
{
    public string? Username { get; } = username;
    public SecretLease? Password { get; } = password;
    public bool IsValid => valid();
    public void Dispose() => Password?.Dispose();
}
