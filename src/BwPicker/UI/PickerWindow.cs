using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BwPicker;

enum PickAction { Type, CopyUsername, CopyPassword }

/// <summary>Search popup shown over the focused app.</summary>
sealed class PickerWindow : PanelWindow
{
    const int MaxVisibleRows = 6;

    static readonly (string[] Keys, string Label)[] Hints =
    [
        (["Enter"], "Type both"),
        (["Tab"], "Username only"),
        (["Ctrl", "Enter"], "Password only"),
        (["Shift"], "+ submit"),
    ];

    readonly BwClient bw;
    readonly WindowContext target;
    readonly Action<string, Notice> notify;
    readonly Matcher matcher;
    readonly List<Entry> ranked = [];
    readonly TextBox search;
    readonly ResultList results;
    readonly TextBlock count, footerStatus;
    readonly Control hints;
    bool working;

    /// <summary>Closing when focus moves elsewhere; turned off by `--preview`.</summary>
    public bool CloseOnDeactivate { get; set; } = true;

    /// <summary>Sets the search text; used by `--preview`.</summary>
    public string Query { set => search.Text = value; }

    public PickerWindow(BwClient bw, WindowContext target, Action<string, Notice> notify)
    {
        this.bw = bw;
        this.target = target;
        this.notify = notify;
        matcher = new Matcher(target);
        Title = "BwPicker";
        Topmost = true;
        ShowInTaskbar = false;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var close = new Button
        {
            Content = "×", Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        Ui.Neutral(P, close);
        close.Click += (_, _) => Close();

        search = Ui.Field(P, "Search your vault…");
        search.FontSize = 15;
        search.Padding = new Thickness(6, 10);
        search.InnerLeftContent = new TextBlock { Text = "⌕", FontSize = 18, Foreground = Ui.Brush(P.SubtleText), Margin = new Thickness(12, 0, 2, 2), VerticalAlignment = VerticalAlignment.Center };

        count = Ui.Text("", 11.5, P.SubtleText);
        var into = Ui.Text(target.AppName.Length > 0 ? $"Typing into {target.AppName}" : "", 11.5, P.SubtleText);
        into.HorizontalAlignment = HorizontalAlignment.Right;
        var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 10, 4, 0) };
        meta.Children.Add(count);
        Grid.SetColumn(into, 1); meta.Children.Add(into);

        results = new ResultList(P);
        results.ItemActivated += async (_, _) => await Act(PickAction.Type);

        footerStatus = Ui.Text("", 12, P.SubtleText);
        footerStatus.IsVisible = false;
        hints = BuildHints();

        var header = new StackPanel { Margin = new Thickness(20, 18, 20, 12), Spacing = 12 };
        header.Children.Add(Ui.Header(P, "Vault picker", close, title: true));
        header.Children.Add(search);
        header.Children.Add(meta);

        var footer = new Panel { Margin = new Thickness(18, 10), Children = { hints, footerStatus } };
        Content = new StackPanel
        {
            Children = { header, Ui.Divider(P), results, Ui.Divider(P), footer },
        };

        Rank();
        search.Text = matcher.SuggestedQuery(ranked);
        search.TextChanged += (_, _) => Refill();
        Refill();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { if (CloseOnDeactivate && !working) Close(); };
        bw.EntriesChanged += OnEntriesChanged;
        Closed += (_, _) => bw.EntriesChanged -= OnEntriesChanged;
        Opened += (_, _) =>
        {
            PlaceOverTarget();
            Activate();
            search.Focus();
            search.SelectAll();
        };
    }

    Control BuildHints()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (keys, label) in Hints)
        {
            foreach (var key in keys) row.Children.Add(Ui.Keycap(P, key));
            var text = Ui.Text(label, 12, P.SubtleText);
            text.Margin = new Thickness(4, 0, 14, 0);
            row.Children.Add(text);
        }
        return row;
    }

    /// <summary>Centered on the target window's monitor, a fifth of the way down.</summary>
    void PlaceOverTarget()
    {
        var screen = (Native.GetWindowRect(target.Handle, out var r) ? Screens.ScreenFromPoint(new PixelPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) : null)
            ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - size.Width) / 2,
            Math.Min(area.Y + area.Height / 5, Math.Max(area.Y, area.Bottom - size.Height - 16)));
    }

    void Rank()
    {
        ranked.Clear();
        ranked.AddRange(bw.Entries.Select(e => (Entry: e, Score: matcher.Score(e)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Entry));
    }

    void OnEntriesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!bw.Unlocked) { Close(); return; }
        Rank();
        Refill(keepSelection: true);
    });

    void Refill(bool keepSelection = false)
    {
        string query = search.Text ?? "";
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        results.SetItems(ranked.Where(e => Matcher.MatchesQuery(e, words)), keepSelection ? results.SelectedEntry?.Id : null);
        results.EmptyText = ranked.Count == 0 ? "Your vault has no logins yet" : $"No logins match “{query.Trim()}”";
        results.Height = results.Count == 0 ? 96 : Math.Min(results.Count, MaxVisibleRows) * ResultList.RowHeight + 20;
        count.Text = $"{results.Count} {(results.Count == 1 ? "LOGIN" : "LOGINS")}";
    }

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Down: e.Handled = true; results.MoveSelection(1); break;
            case Key.Up: e.Handled = true; results.MoveSelection(-1); break;
            case Key.PageDown: e.Handled = true; results.MoveSelection(MaxVisibleRows); break;
            case Key.PageUp: e.Handled = true; results.MoveSelection(-MaxVisibleRows); break;
            case Key.Enter:
                e.Handled = true;
                await Act(PickAction.Type, ctrl ? TypeFields.PasswordOnly : TypeFields.Both, submit: shift);
                break;
            case Key.Tab:
                e.Handled = true;
                await Act(PickAction.Type, TypeFields.UsernameOnly, submit: shift);
                break;
            case Key.U when ctrl: e.Handled = true; await Act(PickAction.CopyUsername); break;
            case Key.P when ctrl: e.Handled = true; await Act(PickAction.CopyPassword); break;
        }
    }

    void SetStatus(string text)
    {
        footerStatus.Text = text;
        footerStatus.IsVisible = true;
        hints.IsVisible = false;
    }

    async Task Act(PickAction action, TypeFields fields = TypeFields.Both, bool submit = false)
    {
        if (working || results.SelectedEntry is not { } entry) return;
        working = true;
        SetStatus("Getting credentials…");
        try
        {
            using var credential = await bw.GetCredentials(entry.Id);
            if (!credential.IsValid) throw new InvalidOperationException("Vault was locked.");
            switch (action)
            {
                case PickAction.CopyUsername: CopyOrWarn(credential.Username.AsSpan(), "username", credential); break;
                case PickAction.CopyPassword:
                    if (credential.Password is { } password) CopyOrWarn(password.Characters, "password", credential);
                    else notify("This item has no password.", Notice.Warning);
                    break;
                default: TypeInto(credential, fields, submit); break;
            }
        }
        catch (InvalidOperationException ex) { notify(ex.Message, Notice.Error); }
        Close();
    }

    void CopyOrWarn(ReadOnlySpan<char> value, string what, CredentialLease credential)
    {
        if (value.IsEmpty) notify($"This item has no {what}.", Notice.Warning);
        else SecureClipboard.Set(value, () => credential.IsValid);
    }

    void TypeInto(CredentialLease credential, TypeFields fields, bool submit)
    {
        if (target.BlocksTyping)
        {
            notify($"{(target.AppName.Length > 0 ? target.AppName : "This app")} runs as administrator, so Windows blocks typing into it. " +
                "Use Ctrl+U / Ctrl+P to copy instead, or install BwPicker for administrator apps (see README).", Notice.Warning);
            return;
        }
        if (fields != TypeFields.UsernameOnly && (credential.Password == null || credential.Password.Characters.IsEmpty))
        {
            notify("This item has no password.", Notice.Warning);
            return;
        }
        if (fields == TypeFields.UsernameOnly && string.IsNullOrEmpty(credential.Username))
        {
            notify("This item has no username.", Notice.Warning);
            return;
        }
        Typer.TypeCredentials(target, credential, submit, fields);
    }
}
