using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BwPicker;

/// <summary>
/// Finds the Bitwarden CLI (bw) and checks it can only have been put there by root or this user. Linux builds of
/// bw aren't code-signed, so instead of a signature check the executable and every folder above it must be owned
/// by root or the current user and not writable by anyone else, as an attacker would need to replace it.
/// </summary>
sealed class TrustedCli : IDisposable
{
    readonly FileStream file;
    public string Path { get; }
    TrustedCli(FileStream file, string path) { this.file = file; Path = path; }

    /// <summary>Where BwPicker's own installer puts bw.</summary>
    internal static string ManagedPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BwPicker", "cli", "bw");

    /// <summary>A bw executable exists in a known location (its ownership is still checked before every use).</summary>
    public static bool IsInstalled => Candidates().Any(File.Exists);

    public static TrustedCli Open()
    {
        foreach (string candidate in Candidates().Distinct())
        {
            if (!File.Exists(candidate)) continue;
            string? target;
            try { target = TrustedLinkTarget(candidate); }
            catch (IOException) { continue; }
            if (target == null || !Trusted(target, isFile: true))
                throw new InvalidOperationException($"{candidate} or a folder above it can be changed by other users, so BwPicker won't run it. " +
                    "Install the Bitwarden CLI somewhere only you or root can write to.");
            try { return new TrustedCli(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read), candidate); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The Bitwarden executable could not be opened.");
            }
        }
        throw new InvalidOperationException("Install the Bitwarden CLI (bw) to use this app.");
    }

    static IEnumerable<string> Candidates()
    {
        yield return ManagedPath;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return System.IO.Path.Combine(home, ".local", "bin", "bw");
        yield return "/usr/local/bin/bw";
        yield return "/usr/bin/bw";
        yield return "/snap/bin/bw";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (System.IO.Path.IsPathFullyQualified(directory)) yield return System.IO.Path.Combine(directory, "bw");
    }

    /// <summary>
    /// Follows <paramref name="path"/> through every symlink to the file it finally names. Each link's folder must be
    /// trusted too, or whoever can write there could repoint the link between this check and running the path. Null
    /// if a folder along the way isn't trusted or the chain doesn't end.
    /// </summary>
    internal static string? TrustedLinkTarget(string path)
    {
        string current = System.IO.Path.GetFullPath(path);
        for (int hops = 0; hops < 40; hops++)
        {
            string folder = System.IO.Path.GetDirectoryName(current)!;
            if (!Trusted(folder, isFile: false)) return null;
            if (new FileInfo(current).LinkTarget is not { } link) return current;
            current = System.IO.Path.GetFullPath(link, folder);
        }
        return null;
    }

    /// <summary>
    /// The path and each folder above it are owned by root or this user, with no write access for anyone else. Folders
    /// are checked where they really are (symlinked folders resolved), so a link can't hide a writable one. Group write
    /// is fine for root's group, or for this user's own per-user group (Ubuntu-style, umask 002): named after the user
    /// and with no other members. A primary group shared with other users, like "users", doesn't count.
    /// </summary>
    internal static bool Trusted(string path, bool isFile)
    {
        uint me = geteuid(), myGroup = getegid();
        string full = System.IO.Path.GetFullPath(path);
        string? folder = System.IO.Path.GetDirectoryName(full);
        if (folder != null)
        {
            if (RealPath(folder) is not { } realFolder) return false;
            full = System.IO.Path.Join(realFolder, System.IO.Path.GetFileName(full));
        }
        bool first = true;
        for (string? current = full; current != null; current = System.IO.Path.GetDirectoryName(current))
        {
            if (!Stat(current, out uint owner, out uint group, out uint mode)) return false;
            if (owner != 0 && owner != me) return false;
            if ((mode & 0x02) != 0) return false; // S_IWOTH
            if ((mode & 0x10) != 0 && group != 0 && (group != myGroup || !IsPersonalGroup(group, me))) return false; // S_IWGRP
            if (first && isFile && ((mode & 0xF000) != 0x8000 || (mode & 0x49) == 0)) return false; // a regular, executable file
            first = false;
        }
        return true;
    }

    static readonly object accountLookup = new();

    static string? RealPath(string path)
    {
        IntPtr resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { free(resolved); }
    }

    /// <summary>The group is named after the user and lists no members (nobody else has it as a supplementary group).</summary>
    static bool IsPersonalGroup(uint gid, uint uid)
    {
        // struct passwd and struct group both start with the name; gr_mem follows gr_name, gr_passwd and gr_gid (LP64).
        // getpwuid/getgrgid return static storage, so read them under one lock.
        lock (accountLookup)
        {
            IntPtr user = getpwuid(uid), group = getgrgid(gid);
            if (user == IntPtr.Zero || group == IntPtr.Zero) return false;
            string? userName = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(user));
            string? groupName = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(group));
            IntPtr members = Marshal.ReadIntPtr(group, 3 * IntPtr.Size);
            bool noOtherMembers = members == IntPtr.Zero || Marshal.ReadIntPtr(members) == IntPtr.Zero ||
                (Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(members)) == userName && Marshal.ReadIntPtr(members, IntPtr.Size) == IntPtr.Zero);
            return userName != null && userName == groupName && noOtherMembers;
        }
    }

    static unsafe bool Stat(string path, out uint owner, out uint group, out uint mode)
    {
        byte* buffer = stackalloc byte[256];
        // statx has the same layout on every architecture: stx_uid at offset 20, stx_gid at 24, stx_mode at 28.
        if (statx(-100 /* AT_FDCWD */, path, 0, 0x1B /* STATX_TYPE | STATX_MODE | STATX_UID | STATX_GID */, buffer) != 0)
        {
            owner = group = mode = 0;
            return false;
        }
        owner = *(uint*)(buffer + 20);
        group = *(uint*)(buffer + 24);
        mode = *(ushort*)(buffer + 28);
        return true;
    }

    public void Dispose() => file.Dispose();

    [DllImport("libc", SetLastError = true)] static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] static extern uint getegid();
    [DllImport("libc", SetLastError = true)] static extern unsafe int statx(int dirfd, string path, int flags, uint mask, byte* buffer);
    [DllImport("libc", SetLastError = true)] static extern IntPtr realpath(string path, IntPtr resolved);
    [DllImport("libc")] static extern void free(IntPtr pointer);
    [DllImport("libc", SetLastError = true)] static extern IntPtr getpwuid(uint uid);
    [DllImport("libc", SetLastError = true)] static extern IntPtr getgrgid(uint gid);
}

static class CliEnvironment
{
    internal static ProcessStartInfo Create(string executable, string[] args)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = home,
        };
        psi.Environment.Clear();
        foreach (string name in new[] { "HOME", "USER", "LOGNAME", "LANG", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        psi.Environment["HOME"] = home;
        // Fixed system folders only (snap's launcher and an npm install's node live there).
        psi.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin:/snap/bin";
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
