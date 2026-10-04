namespace BwPicker;

enum PickAction { Type, CopyUsername, CopyPassword }

/// <summary>Search popup shown over the focused app.</summary>
sealed class PickerForm : ThemedForm
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
    readonly Action<string, ToolTipIcon> notify;
    readonly List<Entry> ranked;
    readonly Matcher matcher;
    readonly TextBox search;
    readonly ResultList results;
    readonly DpiFont searchFont = new(Theme.Display, 11f);
    readonly DpiFont smallFont = new(Theme.Body, 8.5f);
    readonly DpiFont keyFont = new(Theme.Body, 8f);
    readonly DpiFont titleFont = new(Theme.Semibold, 12f);
    readonly FlatButton close;
    string? statusText;
    bool working;

    /// <summary>Closing when focus moves elsewhere; turned off by `--preview`.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CloseOnDeactivate { get; set; } = true;

    int HeaderHeight => S(132);
    int FooterHeight => S(42);
    Rectangle SearchBounds => new(S(20), S(64), ClientSize.Width - S(40), S(40));

    public PickerForm(BwClient bw, WindowContext target, Action<string, ToolTipIcon> notify)
    {
        this.bw = bw;
        this.target = target;
        this.notify = notify;

        matcher = new Matcher(target);
        ranked = bw.Entries
            .Select(e => (Entry: e, Score: matcher.Score(e)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();

        Text = "BwPicker";

        search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = searchFont,
            PlaceholderText = "Search your vault…",
            AccessibleName = "Search logins",
        };
        results = new ResultList(Theme);
        close = new FlatButton(Theme, primary: false) { Text = "×", AccessibleName = "Close picker" };
        close.Click += (_, _) => Close();
        Controls.AddRange([search, results, close]);
        bw.EntriesChanged += OnEntriesChanged;

        search.Text = matcher.SuggestedQuery(ranked);
        search.TextChanged += (_, _) => Refill();
        results.ItemActivated += async (_, _) => await Act(PickAction.Type);
        KeyDown += OnKeyDown;
        Deactivate += (_, _) => { if (CloseOnDeactivate && !working) Close(); };
        Shown += (_, _) =>
        {
            Relayout();
            var workArea = Screen.FromHandle(target.Handle).WorkingArea;
            Location = new Point(workArea.Left + (workArea.Width - Width) / 2,
                Math.Min(workArea.Top + workArea.Height / 5, Math.Max(workArea.Top, workArea.Bottom - Height - S(16))));
            Activate(); search.Focus(); search.SelectAll();
        };
        search.GotFocus += (_, _) => Invalidate(SearchBounds);
        search.LostFocus += (_, _) => Invalidate(SearchBounds);
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Relayout(); });

        Refill();
        var area = Screen.FromHandle(target.Handle).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2,
            Math.Min(area.Top + area.Height / 5, Math.Max(area.Top, area.Bottom - Height - S(16))));
    }

    void OnEntriesChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnEntriesChanged(sender, e)); return; }
        if (!bw.Unlocked) { Close(); return; }
        ranked.Clear();
        ranked.AddRange(bw.Entries.Select(entry => (Entry: entry, Score: matcher.Score(entry)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry));
        Refill(preserveSelection: true);
    }

    void Refill(bool preserveSelection = false)
    {
        var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        results.SetItems(ranked.Where(e => Matcher.MatchesQuery(e, words)), preserveSelection ? results.SelectedEntry?.Id : null);
        results.EmptyText = ranked.Count == 0
            ? "Your vault has no logins yet"
            : $"No logins match “{search.Text.Trim()}”";
        Relayout();
    }

    void Relayout()
    {
        searchFont.SetDpi(DeviceDpi);
        smallFont.SetDpi(DeviceDpi);
        keyFont.SetDpi(DeviceDpi);
        titleFont.SetDpi(DeviceDpi);
        results.SetDpi(DeviceDpi);
        var area = Screen.FromHandle(target.Handle).WorkingArea;
        int width = Math.Min(S(600), area.Width - S(32));
        int availableRows = Math.Max(1, (area.Height - S(64) - HeaderHeight - FooterHeight - results.Inset * 2 - 2) / results.RowHeight);
        int rows = Math.Clamp(results.Count, 0, Math.Min(MaxVisibleRows, availableRows));
        int listHeight = rows == 0 ? S(96) : rows * results.RowHeight + results.Inset * 2;
        ClientSize = new Size(width, HeaderHeight + 1 + listHeight + 1 + FooterHeight);

        var field = SearchBounds;
        search.Font = searchFont;
        close.RestoreFont(DeviceDpi);
        search.Location = new Point(field.X + S(44), field.Y + (field.Height - search.PreferredHeight) / 2);
        search.Width = field.Width - S(60);
        close.SetBounds(width - S(48), S(20), S(28), S(28));
        results.Bounds = new Rectangle(0, HeaderHeight + 1, width, listHeight);
        if (IsHandleCreated)
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        Invalidate();
    }

    // Small label naming the app the login will be typed into.
    Rectangle ChipBounds()
    {
        if (string.IsNullOrEmpty(target.AppName)) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(ChipText, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int w = Math.Min(size.Width, S(180)) + S(20);
        int h = S(24);
        return new Rectangle(ClientSize.Width - S(20) - w, S(106), w, h);
    }

    string ChipText => target.AppName.Length > 0 ? $"Typing into {target.AppName}" : "";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);

        Theme.FillRounded(g, Theme.Selected, new Rectangle(S(20), S(18), S(32), S(32)), S(10));
        Theme.DrawShield(g, new Rectangle(S(30), S(25), S(12), S(17)), Theme.Accent);
        DrawText(g, "Vault picker", titleFont, new Rectangle(S(64), S(20), S(300), S(28)), Theme.Text);
        var field = SearchBounds;
        Theme.FillRounded(g, Theme.Surface, field, S(12));
        Theme.DrawRounded(g, search.Focused ? Theme.Accent : Theme.Border,
            new RectangleF(field.X + .5f, field.Y + .5f, field.Width - 1, field.Height - 1), S(12));
        using (var searchPen = new Pen(Theme.SubtleText, S(1.5f)))
        {
            float x = field.X + S(17), y = field.Y + S(12);
            g.DrawEllipse(searchPen, x, y, S(12), S(12));
            g.DrawLine(searchPen, x + S(10), y + S(10), x + S(16), y + S(16));
        }
        DrawText(g, $"{results.Count} {(results.Count == 1 ? "LOGIN" : "LOGINS")}", smallFont,
            new Rectangle(S(24), S(106), S(220), S(24)), Theme.SubtleText);

        var chip = ChipBounds();
        if (!chip.IsEmpty)
        {
            DrawText(g, ChipText, smallFont, chip, Theme.SubtleText, TextFormatFlags.HorizontalCenter);
        }

        using var divider = new Pen(Theme.Divider);
        int footerTop = ClientSize.Height - FooterHeight - 1;
        g.DrawLine(divider, 0, HeaderHeight, ClientSize.Width, HeaderHeight);
        g.DrawLine(divider, 0, footerTop, ClientSize.Width, footerTop);

        var footer = new Rectangle(0, footerTop + 1, ClientSize.Width, FooterHeight);
        if (statusText != null)
            DrawText(g, statusText, smallFont, Rectangle.Inflate(footer, -S(18), 0), Theme.SubtleText);
        else
            DrawHints(g, footer);
    }

    void DrawHints(Graphics g, Rectangle footer)
    {
        const TextFormatFlags measure = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int x = footer.X + S(18);
        int capHeight = S(20);
        int capTop = footer.Y + (footer.Height - capHeight) / 2;

        foreach (var (keys, label) in Hints)
        {
            foreach (var key in keys)
            {
                int w = Math.Max(TextRenderer.MeasureText(key, keyFont, Size.Empty, measure).Width + S(12), capHeight);
                var cap = new Rectangle(x, capTop, w, capHeight);
                Theme.FillRounded(g, Theme.Surface, cap, S(4));
                Theme.DrawRounded(g, Theme.Border, new RectangleF(cap.X + .5f, cap.Y + .5f, cap.Width - 1, cap.Height - 1), S(4));
                DrawText(g, key, keyFont, cap, Theme.SubtleText, TextFormatFlags.HorizontalCenter);
                x = cap.Right + S(4);
            }
            int labelWidth = TextRenderer.MeasureText(label, smallFont, Size.Empty, measure).Width;
            TextRenderer.DrawText(g, label, smallFont, new Rectangle(x + S(4), footer.Y, labelWidth + S(2), footer.Height), Theme.SubtleText,
                measure | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            x += S(4) + labelWidth + S(16);
        }
    }

    void SetStatus(string? text)
    {
        statusText = text;
        Invalidate(new Rectangle(0, ClientSize.Height - FooterHeight, ClientSize.Width, FooterHeight));
        Update();
    }

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;
            case Keys.Down or Keys.Up:
                results.MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
                break;
            case Keys.PageDown or Keys.PageUp:
                results.MoveSelection(e.KeyCode == Keys.PageDown ? MaxVisibleRows : -MaxVisibleRows);
                break;
            case Keys.Enter:
                if (close.Focused) return;
                e.SuppressKeyPress = true;
                await Act(PickAction.Type, e.Control ? TypeFields.PasswordOnly : TypeFields.Both, submit: e.Shift);
                return;
            case Keys.U when e.Control:
                e.SuppressKeyPress = true;
                await Act(PickAction.CopyUsername);
                return;
            case Keys.P when e.Control:
                e.SuppressKeyPress = true;
                await Act(PickAction.CopyPassword);
                return;
            default:
                return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    // Tab never reaches KeyDown (WinForms uses it for focus navigation), so handle it here.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & ~Keys.Shift) == Keys.Tab && !close.Focused)
        {
            _ = Act(PickAction.Type, TypeFields.UsernameOnly, submit: (keyData & Keys.Shift) != 0);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
                case PickAction.CopyUsername:
                    CopyOrWarn(credential.Username.AsSpan(), "username", credential);
                    break;
                case PickAction.CopyPassword:
                    if (credential.Password is { } password) CopyOrWarn(password.Characters, "password", credential);
                    else notify("This item has no password.", ToolTipIcon.Warning);
                    break;
                default:
                    TypeInto(credential, fields, submit);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            notify(ex.Message, ToolTipIcon.Error);
        }
        Close();
    }

    void CopyOrWarn(ReadOnlySpan<char> value, string what, CredentialLease credential)
    {
        if (value.IsEmpty) notify($"This item has no {what}.", ToolTipIcon.Warning);
        else SecureClipboard.Set(value, () => credential.IsValid);
    }

    void TypeInto(CredentialLease credential, TypeFields fields, bool submit)
    {
        if (target.BlocksTyping)
        {
            notify($"{(target.AppName.Length > 0 ? target.AppName : "This app")} runs as administrator, so Windows blocks typing into it. " +
                "Use Ctrl+U / Ctrl+P to copy instead, or install BwPicker for administrator apps (see README).", ToolTipIcon.Warning);
            return;
        }
        if (fields != TypeFields.UsernameOnly && (credential.Password == null || credential.Password.Characters.IsEmpty))
        {
            notify("This item has no password.", ToolTipIcon.Warning);
            return;
        }
        if (fields == TypeFields.UsernameOnly && string.IsNullOrEmpty(credential.Username))
        {
            notify("This item has no username.", ToolTipIcon.Warning);
            return;
        }
        Typer.TypeCredentials(target, credential, submit, fields);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bw.EntriesChanged -= OnEntriesChanged;
            searchFont.Dispose();
            smallFont.Dispose();
            keyFont.Dispose();
            titleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
