namespace BwPicker;

enum PickAction { Type, TypeAndSubmit, CopyUsername, CopyPassword }

/// <summary>Search popup shown over the focused app.</summary>
sealed class PickerForm : ThemedForm
{
    const int MaxVisibleRows = 7;

    static readonly (string[] Keys, string Label)[] Hints =
    [
        (["Enter"], "Type"),
        (["Shift", "Enter"], "Type and submit"),
        (["Ctrl", "U"], "Copy username"),
        (["Ctrl", "P"], "Copy password"),
    ];

    readonly BwClient bw;
    readonly WindowContext target;
    readonly Action<string, ToolTipIcon> notify;
    readonly List<Entry> ranked;
    readonly TextBox search;
    readonly ResultList results;
    readonly Font searchFont = Theme.Display(13f);
    readonly Font iconFont = Theme.Icons(12f);
    readonly Font smallFont = Theme.Body(8.5f);
    readonly Font keyFont = Theme.Body(8f);
    string? statusText;
    bool working;

    /// <summary>Closing when focus moves elsewhere; turned off by `--preview`.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CloseOnDeactivate { get; set; } = true;

    int HeaderHeight => S(58);
    int FooterHeight => S(40);

    public PickerForm(BwClient bw, WindowContext target, Action<string, ToolTipIcon> notify)
    {
        this.bw = bw;
        this.target = target;
        this.notify = notify;

        var matcher = new Matcher(target);
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
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = searchFont,
            PlaceholderText = "Search logins",
        };
        results = new ResultList(Theme);
        Controls.AddRange([search, results]);

        search.Text = matcher.SuggestedQuery(ranked);
        search.TextChanged += (_, _) => Refill();
        results.ItemActivated += async (_, _) => await Act(PickAction.Type);
        KeyDown += OnKeyDown;
        Deactivate += (_, _) => { if (CloseOnDeactivate && !working) Close(); };
        Shown += (_, _) => { Activate(); search.Focus(); search.SelectAll(); };
        DpiChanged += (_, _) => Relayout();

        Refill();
        var area = Screen.FromHandle(target.Handle).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 5);
    }

    void Refill()
    {
        results.SetItems(ranked.Where(e => Matcher.MatchesQuery(e, search.Text)).Take(200));
        results.EmptyText = ranked.Count == 0
            ? "Your vault has no logins yet"
            : $"No logins match “{search.Text.Trim()}”";
        Relayout();
    }

    void Relayout()
    {
        int width = S(660);
        int rows = Math.Clamp(results.Count, 0, MaxVisibleRows);
        int listHeight = rows == 0 ? S(84) : rows * results.RowHeight + results.Inset * 2;
        ClientSize = new Size(width, HeaderHeight + 1 + listHeight + 1 + FooterHeight);

        int chipWidth = ChipBounds().Width;
        int searchLeft = S(54);
        search.Location = new Point(searchLeft, (HeaderHeight - search.PreferredHeight) / 2 + S(1));
        search.Width = width - searchLeft - S(20) - (chipWidth > 0 ? chipWidth + S(12) : 0);
        results.Bounds = new Rectangle(0, HeaderHeight + 1, width, listHeight);
        Invalidate();
    }

    // Small label naming the app the login will be typed into.
    Rectangle ChipBounds()
    {
        if (string.IsNullOrEmpty(target.AppName)) return Rectangle.Empty;
        var size = TextRenderer.MeasureText(ChipText, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int w = Math.Min(size.Width, S(180)) + S(20);
        int h = S(24);
        return new Rectangle(ClientSize.Width - S(18) - w, (HeaderHeight - h) / 2, w, h);
    }

    string ChipText => target.AppName.Length > 0 ? $"into {target.AppName}" : "";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PrepareGraphics(g);
        g.Clear(Theme.Background);

        DrawText(g, Theme.SearchGlyph, iconFont, new Rectangle(S(20), 0, S(24), HeaderHeight), Theme.SubtleText, TextFormatFlags.HorizontalCenter);

        var chip = ChipBounds();
        if (!chip.IsEmpty)
        {
            Theme.FillRounded(g, Theme.Surface, chip, chip.Height / 2f);
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
            x += S(4) + labelWidth + S(22);
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
                e.SuppressKeyPress = true;
                await Act(e.Shift ? PickAction.TypeAndSubmit : PickAction.Type);
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

    async Task Act(PickAction action)
    {
        if (working || results.SelectedEntry is not { } entry) return;
        working = true;
        SetStatus("Getting credentials…");

        try
        {
            var (username, password) = await bw.GetCredentials(entry.Id);
            switch (action)
            {
                case PickAction.CopyUsername:
                    CopyOrWarn(username, "username");
                    break;
                case PickAction.CopyPassword:
                    CopyOrWarn(password, "password");
                    break;
                default:
                    TypeInto(username, password, submit: action == PickAction.TypeAndSubmit);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            notify(ex.Message, ToolTipIcon.Error);
        }
        Close();
    }

    void CopyOrWarn(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) notify($"This item has no {what}.", ToolTipIcon.Warning);
        else SecureClipboard.Set(value);
    }

    void TypeInto(string? username, string? password, bool submit)
    {
        if (string.IsNullOrEmpty(password))
        {
            notify("This item has no password.", ToolTipIcon.Warning);
            return;
        }
        // Never type a password unless the original window is confirmed to be in front again.
        if (!Typer.FocusAndWait(target.Handle))
        {
            notify("Couldn't switch back to the app, so nothing was typed.", ToolTipIcon.Warning);
            return;
        }
        Typer.WaitForModifiersReleased();
        if (!string.IsNullOrEmpty(username))
        {
            Typer.TypeText(username);
            Typer.PressKey(Native.VK_TAB);
        }
        Typer.TypeText(password);
        if (submit) Typer.PressKey(Native.VK_RETURN);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            searchFont.Dispose();
            iconFont.Dispose();
            smallFont.Dispose();
            keyFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
