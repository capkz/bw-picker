using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BwPicker;

/// <summary>Holds a read-only file handle while checking and launching the signed Bitwarden executable.</summary>
sealed class TrustedCli : IDisposable
{
    static readonly object gate = new();
    static readonly Dictionary<string, byte[]> verifiedHashes = new(StringComparer.OrdinalIgnoreCase);
    readonly FileStream file;
    public string Path { get; }
    TrustedCli(FileStream file, string path) { this.file = file; Path = path; }

    /// <summary>A bw.exe exists in a known location (its signature is still checked before every use).</summary>
    public static bool IsInstalled => Candidates().Any(File.Exists);

    public static TrustedCli Open()
    {
        try { return OpenVerified(); }
        catch (IOException) { throw new InvalidOperationException("The Bitwarden executable could not be opened safely. Reinstall or close its updater and try again."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Access to the Bitwarden executable was denied."); }
        catch (CryptographicException) { throw new InvalidOperationException("The Bitwarden publisher certificate could not be verified."); }
    }

    static TrustedCli OpenVerified()
    {
        foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var path = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(), path, (uint)path.Capacity, 0);
                if (length == 0 || length >= path.Capacity) throw new InvalidOperationException("Could not resolve the Bitwarden executable.");
                string resolved = path.ToString();
                byte[] hash = SHA256.HashData(stream);
                lock (gate)
                {
                    if (!verifiedHashes.TryGetValue(resolved, out var previous) || !CryptographicOperations.FixedTimeEquals(previous, hash))
                    {
                        Verify(resolved, stream.SafeFileHandle.DangerousGetHandle());
                        verifiedHashes[resolved] = hash;
                    }
                }
                return new TrustedCli(stream, resolved);
            }
            catch { stream.Dispose(); throw; }
        }
        throw new InvalidOperationException("Install the official signed Bitwarden CLI for Windows to use this app.");
    }

    static IEnumerable<string> Candidates()
    {
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "bw.exe");
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bitwarden CLI", "bw.exe");
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string absolute = directory.Trim().Trim('"');
            if (System.IO.Path.IsPathFullyQualified(absolute)) yield return System.IO.Path.Combine(absolute, "bw.exe");
        }
    }

    internal static void Verify(string path, IntPtr handle = default)
    {
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, Handle = handle };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(info, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, Revocation = 1,
            UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x80,
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new InvalidOperationException("Bitwarden CLI signature validation failed. Reinstall the official CLI.");
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "Bitwarden Inc.")
                throw new InvalidOperationException("The CLI executable is not signed by Bitwarden Inc.");
        }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }

    public void Dispose() => file.Dispose();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip;
        public uint UiChoice, Revocation, UnionChoice; public IntPtr File;
        public uint StateAction; public IntPtr StateData, Url;
        public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint capacity, uint flags);
}

static class CliEnvironment
{
    internal static ProcessStartInfo Create(string executable, string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        psi.Environment.Clear();
        foreach (string name in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData", "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH" })
            if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        psi.Environment["SystemRoot"] = psi.Environment["WINDIR"] = windows;
        psi.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        psi.Environment["BW_NOINTERACTION"] = "true";
        psi.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";
        if (Environment.GetEnvironmentVariable("BITWARDENCLI_APPDATA_DIR") is { Length: > 0 } profile)
        {
            if (!System.IO.Path.IsPathFullyQualified(profile)) throw new InvalidOperationException("The CLI profile path must be absolute.");
            psi.Environment["BITWARDENCLI_APPDATA_DIR"] = profile;
        }
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--nointeraction");
        return psi;
    }
}
