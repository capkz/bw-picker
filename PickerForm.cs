namespace BwPicker;

enum PickAction { Type, TypeAndSubmit, CopyUsername, CopyPassword }

/// <summary>Search popup shown over the focused app.</summary>
sealed class PickerForm : Form
{
    const string Help = "Enter: type  ·  Shift+Enter: type + submit  ·  Ctrl+U / Ctrl+P: copy  ·  Esc";

    readonly BwClient bw;
    readonly WindowContext target;
    readonly Action<string, ToolTipIcon> notify;
    readonly List<Entry> ranked;
    readonly TextBox search;
    readonly ListView list;
    readonly Label status;
    bool working;

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
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Font;
        Size = new Size(540, 360);
        Padding = new Padding(1);
        BackColor = SystemColors.ActiveBorder;

        search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search vault…", BorderStyle = BorderStyle.FixedSingle };
        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.None,
            BorderStyle = BorderStyle.None,
        };
        list.Columns.Add("Name", 270);
        list.Columns.Add("Username", 250);
        status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = Help,
            ForeColor = SystemColors.GrayText,
            BackColor = SystemColors.Window,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };
        // Fill must be first so it gets the space left by the docked top/bottom controls.
        Controls.AddRange([list, search, status]);

        var area = Screen.FromHandle(target.Handle).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);

        search.Text = matcher.SuggestedQuery(ranked);
        search.TextChanged += (_, _) => Refill();
        list.DoubleClick += async (_, _) => await Act(PickAction.Type);
        KeyDown += OnKeyDown;
        Deactivate += (_, _) => { if (!working) Close(); };
        Shown += (_, _) => { Activate(); search.Focus(); search.SelectAll(); };

        Refill();
    }

    void Refill()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var e in ranked.Where(e => Matcher.MatchesQuery(e, search.Text)).Take(200))
            list.Items.Add(new ListViewItem([e.Name, e.Username ?? ""]) { Tag = e });
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        list.EndUpdate();
        status.Text = list.Items.Count == 0 ? "No matches" : Help;
    }

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;
            case Keys.Down or Keys.Up:
                MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
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

    void MoveSelection(int delta)
    {
        if (list.Items.Count == 0) return;
        int current = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : 0;
        int next = Math.Clamp(current + delta, 0, list.Items.Count - 1);
        list.Items[next].Selected = true;
        list.EnsureVisible(next);
    }

    async Task Act(PickAction action)
    {
        if (working || list.SelectedItems.Count == 0) return;
        var entry = (Entry)list.SelectedItems[0].Tag!;
        working = true;
        status.Text = "Fetching…";

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
}
