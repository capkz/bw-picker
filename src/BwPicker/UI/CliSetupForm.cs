using System.Diagnostics;

namespace BwPicker;

/// <summary>Shown when the Bitwarden CLI is missing: installs it with winget, or links to the download page.</summary>
sealed class CliSetupForm : ThemedForm
{
    readonly DpiFont titleFont = new(Theme.Semibold, 17f);
    readonly DpiFont bodyFont = new(Theme.Body, 9.5f);
    readonly FlatButton install, cancel;
    readonly bool canInstall = CliInstaller.CanInstall;
    string message = "";
    bool messageIsError, working;

    const string Explanation =
        "BwPicker uses the official Bitwarden command-line tool to sign in and sync your vault. It isn't installed on this PC yet.";

    public CliSetupForm()
    {
        Text = "Bitwarden CLI needed";
        ShowInTaskbar = true;
        cancel = new FlatButton(Theme, primary: false) { Text = "Cancel" };
        install = new FlatButton(Theme, primary: true) { Text = canInstall ? "Install" : "Open download page" };
        foreach (var c in new Control[] { cancel, install }) c.BackColor = Theme.Background;
        Controls.AddRange([cancel, install]);

        cancel.Click += (_, _) => { if (!working) DialogResult = DialogResult.Cancel; };
        install.Click += async (_, _) => await Run();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && !working) DialogResult = DialogResult.Cancel; };
        FormClosing += (_, e) => { if (working) e.Cancel = true; }; // let winget finish
        MakeDraggable(this);
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Relayout(); });

        if (!canInstall) message = "winget isn't available here, so install the CLI from Bitwarden's site, then try again.";
        Relayout();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 4));
        Shown += (_, _) => { Relayout(); Activate(); install.Focus(); };
    }

    void Relayout()
    {
        titleFont.SetDpi(DeviceDpi); bodyFont.SetDpi(DeviceDpi);
        cancel.RestoreFont(DeviceDpi); install.RestoreFont(DeviceDpi);
        int pad = S(24), width = S(380), buttonWidth = (width - pad * 2 - S(10)) / 2, top = S(214);
        cancel.SetBounds(pad, top, buttonWidth, S(36));
        install.SetBounds(cancel.Right + S(10), top, buttonWidth, S(36));
        ClientSize = new Size(width, top + S(36 + 24));
        Invalidate();
    }

    async Task Run()
    {
        if (working) return;
        if (!canInstall)
        {
            Process.Start(new ProcessStartInfo(CliInstaller.DownloadPage) { UseShellExecute = true })?.Dispose();
            return;
        }
        working = true;
        install.Enabled = cancel.Enabled = false;
        SetMessage("Installing the Bitwarden CLI with winget… this can take a minute.", false);
        try
        {
            await CliInstaller.Install();
            working = false;
            DialogResult = DialogResult.OK;
        }
        catch (InvalidOperationException ex)
        {
            working = false;
            if (IsDisposed) return;
            SetMessage(ex.Message, true);
            install.Enabled = cancel.Enabled = true;
        }
    }

    void SetMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);
        int pad = S(24), width = ClientSize.Width - pad * 2;
        const TextFormatFlags wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;

        Theme.FillRounded(g, Theme.Selected, new Rectangle(pad, S(20), S(32), S(32)), S(10));
        Theme.DrawShield(g, new Rectangle(pad + S(10), S(27), S(12), S(17)), Theme.Accent);
        DrawText(g, "VAULT PICKER", bodyFont, new Rectangle(S(68), S(22), S(250), S(28)), Theme.SubtleText);
        DrawText(g, "Bitwarden CLI needed", titleFont, new Rectangle(pad, S(64), width, S(30)), Theme.Text);
        TextRenderer.DrawText(g, Explanation, bodyFont, new Rectangle(pad, S(100), width, S(52)), Theme.SubtleText, wrap);
        if (message.Length > 0)
            TextRenderer.DrawText(g, message, bodyFont, new Rectangle(pad, S(156), width, S(52)),
                messageIsError ? Theme.Critical : Theme.Text, wrap);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); }
        base.Dispose(disposing);
    }
}
