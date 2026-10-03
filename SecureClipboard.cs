using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BwPicker;

sealed class ClipboardOwnership
{
    public uint Sequence { get; private set; }
    public void Mark(uint sequence) => Sequence = sequence;
    public bool Matches(uint current) => Sequence != 0 && Sequence == current;
    public void Forget() => Sequence = 0;
}

/// <summary>Publishes native clipboard data without retaining a plaintext comparison copy.</summary>
static class SecureClipboard
{
    static readonly object gate = new();
    static readonly ClipboardOwnership ownership = new();
    static NativeWindow? owner;
    static System.Threading.Timer? clearTimer;
    static long deadline;

    public static void Set(ReadOnlySpan<char> text, Func<bool> valid)
    {
        lock (gate)
        {
            if (!valid()) throw new InvalidOperationException("Vault was locked; nothing was copied.");
            owner ??= CreateOwner();
            if (!OpenClipboard(owner.Handle)) throw new InvalidOperationException("Clipboard is busy. Please try copying again.");
            try
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("Could not clear the clipboard.");
                ownership.Forget();
                SetBlock(Register("ExcludeClipboardContentFromMonitorProcessing"), new byte[4]);
                SetBlock(Register("CanIncludeInClipboardHistory"), new byte[4]);
                SetBlock(Register("CanUploadToCloudClipboard"), new byte[4]);
                if (!valid()) throw new InvalidOperationException("Vault was locked; nothing was copied.");
                SetText(text);
                ownership.Mark(GetClipboardSequenceNumber());
                if (!valid()) { EmptyClipboard(); ownership.Forget(); throw new InvalidOperationException("Vault was locked; the copied credential was cleared."); }
            }
            catch { EmptyClipboard(); throw; }
            finally { CloseClipboard(); }
            deadline = Environment.TickCount64 + 30_000;
            clearTimer?.Dispose();
            clearTimer = new System.Threading.Timer(_ =>
            {
                if (Environment.TickCount64 >= Interlocked.Read(ref deadline)) ClearOwned();
            }, null, 30_000, 1_000);
        }
    }

    public static void ClearOwned()
    {
        lock (gate)
        {
            if (owner == null || ownership.Sequence == 0) return;
            deadline = 0;
            if (!ownership.Matches(GetClipboardSequenceNumber()) || GetClipboardOwner() != owner.Handle)
            {
                ownership.Forget(); clearTimer?.Dispose(); clearTimer = null; return;
            }
            if (!OpenClipboard(owner.Handle)) { clearTimer?.Change(1_000, 1_000); return; }
            try
            {
                if (ownership.Matches(GetClipboardSequenceNumber()) && GetClipboardOwner() == owner.Handle && !EmptyClipboard())
                { clearTimer?.Change(1_000, 1_000); return; }
                ownership.Forget(); clearTimer?.Dispose(); clearTimer = null;
            }
            finally { CloseClipboard(); }
        }
    }

    static NativeWindow CreateOwner()
    {
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        return window;
    }
    static uint Register(string name)
    {
        uint format = RegisterClipboardFormat(name);
        if (format == 0) throw new InvalidOperationException("Could not register clipboard privacy controls.");
        return format;
    }
    static unsafe void SetText(ReadOnlySpan<char> text)
    {
        int bytes = checked((text.Length + 1) * sizeof(char));
        IntPtr block = GlobalAlloc(2, (nuint)bytes);
        if (block == IntPtr.Zero) throw new InvalidOperationException("Could not allocate clipboard data.");
        bool transferred = false;
        try
        {
            IntPtr pointer = GlobalLock(block);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Could not lock clipboard data.");
            try
            {
                var destination = new Span<byte>((void*)pointer, bytes);
                destination.Clear();
                MemoryMarshal.AsBytes(text).CopyTo(destination);
            }
            finally { GlobalUnlock(block); }
            transferred = SetClipboardData(13, block) != IntPtr.Zero;
            if (!transferred) throw new InvalidOperationException("Could not copy the credential.");
        }
        finally { if (!transferred) ZeroAndFree(block, bytes); }
    }
    static unsafe void SetBlock(uint format, ReadOnlySpan<byte> bytes)
    {
        IntPtr block = GlobalAlloc(2, (nuint)bytes.Length);
        if (block == IntPtr.Zero) throw new InvalidOperationException("Could not allocate clipboard privacy controls.");
        bool transferred = false;
        try
        {
            IntPtr pointer = GlobalLock(block);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Could not lock clipboard privacy controls.");
            try { bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length)); }
            finally { GlobalUnlock(block); }
            transferred = SetClipboardData(format, block) != IntPtr.Zero;
            if (!transferred) throw new InvalidOperationException("Could not apply clipboard privacy controls.");
        }
        finally { if (!transferred) ZeroAndFree(block, bytes.Length); }
    }
    static unsafe void ZeroAndFree(IntPtr block, int length)
    {
        IntPtr pointer = GlobalLock(block);
        if (pointer != IntPtr.Zero)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)pointer, length)); GlobalUnlock(block);
        }
        GlobalFree(block);
    }

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr block);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr block);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr block);
}
