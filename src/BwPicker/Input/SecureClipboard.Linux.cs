using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BwPicker;

/// <summary>
/// Copies through xclip (X11) or wl-copy (Wayland). The helper serves the clipboard until another app copies
/// something, then exits; while it still runs, the clipboard holds BwPicker's copy, so clearing it means ending
/// the helper. The credential reaches it through a pipe, never as a .NET string or a command-line argument.
/// </summary>
static class SecureClipboard
{
    static readonly object gate = new();
    static Process? owner;
    static System.Threading.Timer? clearTimer;

    public static void Set(ReadOnlySpan<char> text, Func<bool> valid)
    {
        lock (gate)
        {
            if (!valid()) throw new InvalidOperationException("Vault was locked; nothing was copied.");
            ClearOwned();
            var psi = CreateHelper();
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the clipboard helper.");
            byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
            try
            {
                int length = Encoding.UTF8.GetBytes(text, bytes);
                using (var input = process.StandardInput.BaseStream)
                {
                    input.Write(bytes, 0, length);
                }
            }
            catch (IOException)
            {
                Kill(process);
                throw new InvalidOperationException("The clipboard helper stopped unexpectedly.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                ArrayPool<byte>.Shared.Return(bytes);
            }
            owner = process;
            if (!valid()) { ClearOwned(); throw new InvalidOperationException("Vault was locked; the copied credential was cleared."); }
            clearTimer?.Dispose();
            clearTimer = new System.Threading.Timer(_ => ClearOwned(), null, 30_000, Timeout.Infinite);
        }
    }

    public static void ClearOwned()
    {
        lock (gate)
        {
            clearTimer?.Dispose();
            clearTimer = null;
            if (owner == null) return;
            Kill(owner);
            owner = null;
        }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); process.WaitForExit(1000); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    static ProcessStartInfo CreateHelper()
    {
        ProcessStartInfo psi;
        if (X11.IsWayland && Find("wl-copy") is { } wlCopy)
        {
            // --foreground: stay attached so ending the process clears the clipboard.
            psi = new ProcessStartInfo(wlCopy) { ArgumentList = { "--foreground", "--type", "text/plain;charset=utf-8" } };
        }
        else if (Find("xclip") is { } xclip)
        {
            // -quiet keeps xclip in the foreground; it serves the selection until another app takes it.
            psi = new ProcessStartInfo(xclip) { ArgumentList = { "-quiet", "-selection", "clipboard", "-target", "UTF8_STRING", "-in" } };
        }
        else throw new InvalidOperationException(X11.IsWayland
            ? "Copying needs wl-clipboard. Install it with your package manager (e.g. sudo apt install wl-clipboard)."
            : "Copying needs xclip. Install it with your package manager (e.g. sudo apt install xclip).");
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return psi;
    }

    static string? Find(string tool) =>
        new[] { "/usr/bin", "/usr/local/bin", "/bin" }.Select(dir => Path.Combine(dir, tool)).FirstOrDefault(File.Exists);
}
