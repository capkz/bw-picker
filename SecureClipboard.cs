namespace BwPicker;

/// <summary>Copies secrets while keeping them out of Win+V history and cloud clipboard, and clears them after 30s.</summary>
static class SecureClipboard
{
    static System.Windows.Forms.Timer? clearTimer;
    static string? lastCopied;

    public static void Set(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true);

        lastCopied = text;
        clearTimer?.Dispose();
        clearTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        clearTimer.Tick += (_, _) =>
        {
            clearTimer.Stop();
            try
            {
                // Leave the clipboard alone if the user has copied something else since.
                if (Clipboard.ContainsText() && Clipboard.GetText() == lastCopied) Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
            lastCopied = null;
        };
        clearTimer.Start();
    }
}
