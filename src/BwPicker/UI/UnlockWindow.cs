using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BwPicker;

enum UnlockResult { Cancelled, Unlocked, NeedsSignIn }

sealed class UnlockWindow : PanelWindow
{
    readonly BwClient bw;
    readonly TextBox password;
    readonly Button unlock, cancel;
    readonly TextBlock subtitle, server, message;
    UnlockResult result = UnlockResult.Cancelled;
    bool unlocking;

    public UnlockWindow(BwClient bw, BwStatus? status = null)
    {
        this.bw = bw;
        Title = "Unlock vault";
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;

        var initial = status ?? bw.CachedStatus;
        subtitle = Ui.Text(initial?.Email ?? "Your Bitwarden account", 13, P.SubtleText);
        server = Ui.Text(ServerName(initial), 12, P.SubtleText);
        server.HorizontalAlignment = HorizontalAlignment.Center;
        password = Ui.Field(P, "Master password", secret: true);
        message = Ui.Message(P);
        cancel = Ui.Button(P, "Cancel", primary: false);
        unlock = Ui.Button(P, "Unlock vault", primary: true);
        cancel.Click += (_, _) => Close();
        unlock.Click += async (_, _) => await TryUnlock();

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        Grid.SetColumn(unlock, 2); buttons.Children.Add(unlock);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 18), Spacing = 6,
            Children =
            {
                Ui.Header(P, "VAULT PICKER"),
                Pad(Ui.Text("Unlock your vault", 22, P.Text, FontWeight.SemiBold), top: 12),
                subtitle,
                Pad(Ui.Text("Master password", 12.5, P.SubtleText), top: 10),
                password, message, buttons, Pad(server, top: 10),
            },
        };

        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter && password.IsFocused) { e.Handled = true; await TryUnlock(); }
        }, RoutingStrategies.Tunnel);
        bw.Revoked += OnRevoked;
        Closed += async (_, _) =>
        {
            bw.Revoked -= OnRevoked;
            password.Text = "";
            if (unlocking && result != UnlockResult.Unlocked) { try { await bw.Lock(); } catch (InvalidOperationException) { } }
        };
        Opened += async (_, _) =>
        {
            Activate();
            password.Focus();
            try
            {
                var account = status ?? await bw.Status();
                subtitle.Text = account.Email ?? "Your Bitwarden account";
                server.Text = ServerName(account);
                if (account.Status == "unauthenticated") { result = UnlockResult.NeedsSignIn; Close(); } // the caller shows sign-in instead
            }
            catch (InvalidOperationException ex) { Ui.SetMessage(P, message, ex.Message, error: true); }
        };
    }

    public Task<UnlockResult> Run() => ShowAndWait(() => result);

    static string ServerName(BwStatus? s) => Uri.TryCreate(s?.ServerUrl, UriKind.Absolute, out var u) ? u.Host : "bitwarden.com";

    static Control Pad(Control c, double top) { c.Margin = new Thickness(0, top, 0, 0); return c; }

    async Task TryUnlock()
    {
        if (string.IsNullOrEmpty(password.Text) || !unlock.IsEnabled) return;
        unlocking = true;
        unlock.IsEnabled = cancel.IsEnabled = password.IsEnabled = false;
        Ui.SetMessage(P, message, "Unlocking vault…", error: false);
        try
        {
            using var masterPassword = new MemorySecret(password.Text.AsSpan());
            password.Text = "";
            await bw.Unlock(masterPassword);
            Ui.SetMessage(P, message, "Loading local logins…", error: false);
            await bw.Load(sync: false);
            if (!bw.Unlocked) throw new InvalidOperationException("Vault was locked. Please try again.");
            result = UnlockResult.Unlocked;
            unlocking = false;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            if (bw.Unlocked) { try { await bw.Lock(); } catch (InvalidOperationException) { } }
            Ui.SetMessage(P, message, ex.Message, error: true);
            unlock.IsEnabled = cancel.IsEnabled = password.IsEnabled = true;
            password.Focus();
        }
        finally { unlocking = false; }
    }

    void OnRevoked(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { password.Text = ""; Close(); });
}
