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
            string target;
            try { target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate; }
            catch (IOException) { continue; }
            if (!Trusted(System.IO.Path.GetDirectoryName(candidate)!, isFile: false) || !Trusted(target, isFile: true))
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
    /// The path and each folder above it are owned by root or this user, with no write access for anyone else. Group
    /// write is fine for root's group or this user's own primary group (Ubuntu-style per-user groups, umask 002).
    /// </summary>
    internal static bool Trusted(string path, bool isFile)
    {
        uint me = geteuid(), myGroup = getegid();
        bool first = true;
        for (string? current = System.IO.Path.GetFullPath(path); current != null; current = System.IO.Path.GetDirectoryName(current))
        {
            if (!Stat(current, out uint owner, out uint group, out uint mode)) return false;
            if (owner != 0 && owner != me) return false;
            if ((mode & 0x02) != 0) return false; // S_IWOTH
            if ((mode & 0x10) != 0 && group != 0 && group != myGroup) return false; // S_IWGRP
            if (first && isFile && ((mode & 0xF000) != 0x8000 || (mode & 0x49) == 0)) return false; // a regular, executable file
            first = false;
        }
        return true;
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
