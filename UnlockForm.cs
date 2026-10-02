namespace BwPicker;

sealed class UnlockForm : Form
{
    public UnlockForm(BwClient bw)
    {
        Text = "Unlock Bitwarden";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(360, 132);

        var label = new Label { Text = "Master password", Location = new Point(12, 12), AutoSize = true };
        var password = new TextBox { UseSystemPasswordChar = true, Location = new Point(12, 36), Width = 336 };
        var message = new Label { Location = new Point(12, 68), Size = new Size(336, 22), ForeColor = Color.Firebrick };
        var unlock = new Button { Text = "Unlock", Location = new Point(192, 96), Width = 75 };
        var cancel = new Button { Text = "Cancel", Location = new Point(273, 96), Width = 75, DialogResult = DialogResult.Cancel };
        Controls.AddRange([label, password, message, unlock, cancel]);
        AcceptButton = unlock;
        CancelButton = cancel;

        unlock.Click += async (_, _) =>
        {
            if (password.TextLength == 0) return;
            unlock.Enabled = password.Enabled = false;
            message.ForeColor = SystemColors.GrayText;
            message.Text = "Unlocking and syncing…";
            try
            {
                await bw.Unlock(password.Text);
                password.Clear();
                await bw.Load(sync: true);
                DialogResult = DialogResult.OK;
            }
            catch (InvalidOperationException ex)
            {
                message.ForeColor = Color.Firebrick;
                message.Text = ex.Message;
                unlock.Enabled = password.Enabled = true;
                password.SelectAll();
                password.Focus();
            }
        };

        Shown += (_, _) => { Activate(); password.Focus(); };
    }
}
