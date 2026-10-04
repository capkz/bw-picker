using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace BwPicker;

/// <summary>Signs the Bitwarden CLI in with email + master password (and a two-step code), or a personal API key.</summary>
sealed class SignInWindow : PanelWindow
{
    static readonly TwoStepMethod[] MethodValues = [TwoStepMethod.Authenticator, TwoStepMethod.Email, TwoStepMethod.YubiKey];

    readonly BwClient bw;
    readonly TextBox email, password, code, clientId, clientSecret;
    readonly ComboBox method;
    readonly Control passwordGroup, apiGroup, methodRow, codeRow;
    readonly TextBlock message, apiHint;
    readonly Button submit, cancel, switchMode;
    bool apiKeyMode, needsCode, needsMethod, working, done;

    public SignInWindow(BwClient bw, string serverName, string? initialEmail = null)
    {
        this.bw = bw;
        Title = "Sign in to Bitwarden";
        Topmost = true;
        Width = 380;
        SizeToContent = SizeToContent.Height;

        email = Ui.Field(P, "you@example.com");
        email.Text = initialEmail ?? "";
        password = Ui.Field(P, "Master password", secret: true);
        code = Ui.Field(P, "Two-step login code");
        clientId = Ui.Field(P, "user.xxxxxxxx-xxxx-…");
        clientSecret = Ui.Field(P, "client_secret", secret: true);
        method = new ComboBox { ItemsSource = new[] { "Authenticator app", "Email", "YubiKey" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        methodRow = Labeled("Two-step method", method);
        codeRow = Labeled("Two-step code", code);
        methodRow.IsVisible = codeRow.IsVisible = false;
        passwordGroup = new StackPanel { Spacing = 6, Children = { Labeled("Email", email), Labeled("Master password", password), methodRow, codeRow } };
        apiHint = Ui.Text("Find it in the web vault: Settings → Security → Keys → View API key. API key sign-in skips the new-device email check.",
            12.5, P.SubtleText, wrap: true);
        apiGroup = new StackPanel { Spacing = 6, IsVisible = false, Children = { Labeled("client_id", clientId), Labeled("client_secret", clientSecret), apiHint } };

        message = Ui.Message(P);
        switchMode = new Button
        {
            Content = "Use an API key instead", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Ui.Brush(P.Accent), Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand),
        };
        switchMode.Click += (_, _) => SetMode(!apiKeyMode);
        cancel = Ui.Button(P, "Cancel", primary: false);
        submit = Ui.Button(P, "Sign in", primary: true);
        cancel.Click += (_, _) => Close();
        submit.Click += async (_, _) => await Submit();

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        Grid.SetColumn(submit, 2); buttons.Children.Add(submit);

        var heading = Ui.Text("Sign in", 22, P.Text, FontWeight.SemiBold);
        heading.Margin = new Thickness(0, 12, 0, 0);
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20), Spacing = 6,
            Children = { Ui.Header(P, "VAULT PICKER"), heading, Ui.Text($"to {serverName}", 13, P.SubtleText), passwordGroup, apiGroup, message, switchMode, buttons },
        };

        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is TextBox) { e.Handled = true; await Submit(); }
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => { password.Text = ""; clientSecret.Text = ""; };
        Opened += (_, _) => { Activate(); FocusFirst(); };
    }

    /// <summary>True once signed in (the vault may still need unlocking after API key sign-in).</summary>
    public Task<bool> Run() => ShowAndWait(() => done);

    Control Labeled(string label, Control field)
    {
        var text = Ui.Text(label, 12.5, P.SubtleText);
        text.Margin = new Thickness(0, 6, 0, 2);
        return new StackPanel { Children = { text, field } };
    }

    void SetMode(bool api)
    {
        apiKeyMode = api;
        passwordGroup.IsVisible = !api;
        apiGroup.IsVisible = api;
        switchMode.Content = api ? "Use email and password instead" : "Use an API key instead";
        Ui.SetMessage(P, message, "", false);
        FocusFirst();
    }

    void FocusFirst() => (apiKeyMode ? clientId : string.IsNullOrEmpty(email.Text) ? email : password).Focus();

    async Task Submit()
    {
        if (working) return;
        working = true;
        Control[] inputs = [email, password, code, clientId, clientSecret, method, submit, switchMode];
        foreach (var c in inputs) c.IsEnabled = false;
        Ui.SetMessage(P, message, apiKeyMode ? "Signing in…" : "Signing in… this can take a few seconds.", false);
        try
        {
            if (apiKeyMode) await SubmitApiKey();
            else await SubmitPassword();
        }
        catch (InvalidOperationException ex) { Ui.SetMessage(P, message, ex.Message, true); }
        finally
        {
            working = false;
            foreach (var c in inputs) c.IsEnabled = true;
        }
    }

    async Task SubmitPassword()
    {
        if (string.IsNullOrEmpty(password.Text)) throw new InvalidOperationException("Enter your master password.");
        using var secret = new MemorySecret(password.Text.AsSpan());
        TwoStepMethod? chosen = needsMethod ? MethodValues[Math.Max(0, method.SelectedIndex)] : null;
        string? token = (needsCode || needsMethod) && !string.IsNullOrWhiteSpace(code.Text) ? code.Text.Trim() : null;

        switch (await bw.SignIn(email.Text ?? "", secret, chosen, token))
        {
            case SignInOutcome.SignedIn:
                password.Text = "";
                Ui.SetMessage(P, message, "Loading logins…", false);
                await bw.Load(sync: false);
                done = true;
                Close();
                break;
            case SignInOutcome.NeedsUnlock:
                password.Text = "";
                done = true;
                Close();
                break;
            case SignInOutcome.NeedsMethod:
                needsMethod = true;
                methodRow.IsVisible = codeRow.IsVisible = true;
                Ui.SetMessage(P, message, "Your account has more than one two-step method. Pick one, then enter its code.", false);
                code.Focus();
                break;
            case SignInOutcome.NeedsCode:
                needsCode = true;
                codeRow.IsVisible = true;
                Ui.SetMessage(P, message, chosen == TwoStepMethod.Email ? "We've emailed you a code. Enter it to finish signing in."
                    : "Enter the code from your two-step login method.", false);
                code.Focus();
                break;
        }
    }

    async Task SubmitApiKey()
    {
        if (string.IsNullOrWhiteSpace(clientId.Text) || string.IsNullOrEmpty(clientSecret.Text))
            throw new InvalidOperationException("Enter both the client_id and client_secret.");
        using var id = new MemorySecret(clientId.Text.Trim().AsSpan());
        using var secret = new MemorySecret(clientSecret.Text.Trim().AsSpan());
        await bw.SignInWithApiKey(id, secret);
        clientSecret.Text = "";
        done = true; // the caller asks for the master password to unlock
        Close();
    }
}
