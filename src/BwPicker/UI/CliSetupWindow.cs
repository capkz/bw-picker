using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BwPicker;

/// <summary>Shown when the Bitwarden CLI is missing: installs it with winget, or links to the download page.</summary>
sealed class CliSetupWindow : PanelWindow
{
    readonly bool canInstall = CliInstaller.CanInstall;
    readonly TextBlock message;
    readonly Button install, cancel;
    bool working, installed;

    public CliSetupWindow()
    {
        Title = "Bitwarden CLI needed";
        Topmost = true;
        Width = 380;
        SizeToContent = SizeToContent.Height;

        message = Ui.Message(P);
        if (!canInstall) Ui.SetMessage(P, message, "winget isn't available here, so install the CLI from Bitwarden's site, then try again.", false);
        cancel = Ui.Button(P, "Cancel", primary: false);
        install = Ui.Button(P, canInstall ? "Install" : "Open download page", primary: true);
        cancel.Click += (_, _) => { if (!working) Close(); };
        install.Click += async (_, _) => await Run();
        Closing += (_, e) => { if (working) e.Cancel = true; }; // let winget finish

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        Grid.SetColumn(install, 2); buttons.Children.Add(install);
        var heading = Ui.Text("Bitwarden CLI needed", 22, P.Text, FontWeight.SemiBold);
        heading.Margin = new Thickness(0, 12, 0, 0);
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20), Spacing = 8,
            Children =
            {
                Ui.Header(P, "VAULT PICKER"), heading,
                Ui.Text("BwPicker uses the official Bitwarden command-line tool to sign in and sync your vault. It isn't installed on this PC yet.", 13, P.SubtleText, wrap: true),
                message, buttons,
            },
        };
        Opened += (_, _) => { Activate(); install.Focus(); };
    }

    /// <summary>True when the CLI got installed.</summary>
    public Task<bool> Ask() => ShowAndWait(() => installed);

    async Task Run()
    {
        if (working) return;
        if (!canInstall)
        {
            Process.Start(new ProcessStartInfo(CliInstaller.DownloadPage) { UseShellExecute = true })?.Dispose();
            return;
        }
        working = true;
        install.IsEnabled = cancel.IsEnabled = false;
        Ui.SetMessage(P, message, "Installing the Bitwarden CLI with winget… this can take a minute.", false);
        try
        {
            await CliInstaller.Install();
            installed = true;
            working = false;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            working = false;
            Ui.SetMessage(P, message, ex.Message, true);
            install.IsEnabled = cancel.IsEnabled = true;
        }
    }
}
