using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace BwPicker;

/// <summary>
/// The subset of Bitwarden's client-side decryption needed to unlock a vault locally, built only on .NET's
/// own primitives: PBKDF2-SHA256 master key, HKDF-SHA256 stretching, and authenticated "type 2" EncStrings
/// (AES-256-CBC with HMAC-SHA256, encrypt-then-MAC). The MAC is always verified, in constant time, before
/// anything is decrypted; unauthenticated legacy types are rejected.
/// </summary>
static class BitwardenCrypto
{
    public const int KeyLength = 64; // 32-byte encryption key followed by 32-byte MAC key

    /// <summary>Thrown when data doesn't authenticate: wrong key (e.g. master password) or tampering.</summary>
    public sealed class AuthenticationFailedException() : CryptographicException("Decryption failed authentication.");

    /// <summary>Master key → 64-byte stretched key (HKDF-Expand with "enc" and "mac"), written to a pinned buffer.</summary>
    public static byte[] DeriveStretchedMasterKey(ReadOnlySpan<char> password, string salt, int iterations)
    {
        if (iterations < 5_000 || iterations > 10_000_000) throw new CryptographicException("Unsupported KDF iteration count.");
        var passwordBytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(password), pinned: true);
        var masterKey = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        try
        {
            Encoding.UTF8.GetBytes(password, passwordBytes);
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, Encoding.UTF8.GetBytes(salt), masterKey, iterations, HashAlgorithmName.SHA256);
            var stretched = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
            HKDF.Expand(HashAlgorithmName.SHA256, masterKey, stretched.AsSpan(0, 32), "enc"u8);
            HKDF.Expand(HashAlgorithmName.SHA256, masterKey, stretched.AsSpan(32, 32), "mac"u8);
            return stretched;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// Decrypts a "2.iv|ciphertext|mac" EncString with a 64-byte key into a pinned array the caller must
    /// zero. Throws <see cref="AuthenticationFailedException"/> if the MAC doesn't match.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<char> encString, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength) throw new CryptographicException("Invalid key length.");
        if (encString.Length < 3 || encString[0] != '2' || encString[1] != '.')
            throw new CryptographicException("Unsupported encryption type.");
        var parts = encString[2..];
        int first = parts.IndexOf('|'), second = first < 0 ? -1 : parts[(first + 1)..].IndexOf('|');
        if (first < 0 || second < 0) throw new CryptographicException("Malformed encrypted value.");
        var ivText = parts[..first];
        var ctText = parts.Slice(first + 1, second);
        var macText = parts[(first + second + 2)..];
        if (macText.IndexOf('|') >= 0 || ctText.Length > 4 * 1024 * 1024) throw new CryptographicException("Malformed encrypted value.");

        Span<byte> iv = stackalloc byte[16];
        Span<byte> mac = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(ivText, iv, out int ivLength) || ivLength != 16 ||
            !Convert.TryFromBase64Chars(macText, mac, out int macLength) || macLength != 32)
            throw new CryptographicException("Malformed encrypted value.");
        var ciphertext = new byte[Base64.GetMaxDecodedFromUtf8Length(ctText.Length)];
        if (!Convert.TryFromBase64Chars(ctText, ciphertext, out int ctLength) || ctLength == 0 || ctLength % 16 != 0)
            throw new CryptographicException("Malformed encrypted value.");

        // Encrypt-then-MAC: authenticate iv || ciphertext before touching the ciphertext.
        Span<byte> expected = stackalloc byte[32];
        var macInput = new byte[16 + ctLength];
        iv.CopyTo(macInput);
        ciphertext.AsSpan(0, ctLength).CopyTo(macInput.AsSpan(16));
        HMACSHA256.HashData(key[32..], macInput, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) throw new AuthenticationFailedException();

        var encKey = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        try
        {
            key[..32].CopyTo(encKey);
            using var aes = Aes.Create();
            aes.Key = encKey;
            var plain = aes.DecryptCbc(ciphertext.AsSpan(0, ctLength), iv, PaddingMode.PKCS7);
            var pinned = GC.AllocateUninitializedArray<byte>(plain.Length, pinned: true);
            plain.CopyTo(pinned, 0);
            CryptographicOperations.ZeroMemory(plain);
            return pinned;
        }
        finally { CryptographicOperations.ZeroMemory(encKey); }
    }

    /// <summary>Decrypts a non-secret text field (name, username, URI).</summary>
    public static string DecryptString(ReadOnlySpan<char> encString, ReadOnlySpan<byte> key)
    {
        var plain = Decrypt(encString, key);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Test helper and documentation of the format: encrypts like the Bitwarden clients do.</summary>
    internal static string Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        Span<byte> iv = stackalloc byte[16];
        RandomNumberGenerator.Fill(iv);
        using var aes = Aes.Create();
        aes.Key = key[..32].ToArray();
        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
        var macInput = new byte[16 + ciphertext.Length];
        iv.CopyTo(macInput);
        ciphertext.CopyTo(macInput, 16);
        var mac = HMACSHA256.HashData(key[32..], macInput);
        return $"2.{Convert.ToBase64String(iv)}|{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(mac)}";
    }
}
