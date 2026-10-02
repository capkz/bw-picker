namespace BwPicker;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--preview"))
        {
            ApplicationConfiguration.Initialize();
            if (args.Contains("--dark")) Theme.ForceDark = true;
            if (args.Contains("--light")) Theme.ForceDark = false;
            ShowPreview(args.Contains("--unlock"), args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault());
            return;
        }

        using var mutex = new Mutex(true, "BwPicker.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }

    static void ShowPreview(bool unlock, string? snapshot)
    {
        if (unlock)
        {
            Run(new UnlockForm(new BwClient(), new BwStatus("locked", "you@example.com", "https://vault.example.com")), snapshot);
            return;
        }
        var bw = BwClient.Preview(
        [
            new("1", "Discord", "you@example.com", ["https://discord.com"]),
            new("2", "Discord (alt)", "alt@example.com", ["discord.com"]),
            new("3", "GitHub", "octocat", ["https://github.com/login"]),
            new("4", "Steam", "gamer_42", ["https://store.steampowered.com"]),
            new("5", "Proxmox", "root", ["https://pve.example.com:8006"]),
            new("6", "Home Assistant", "admin", ["https://ha.example.com"]),
            new("7", "Battle.net", "you@example.com", ["https://battle.net"]),
            new("8", "Wi-Fi router", null, ["http://192.168.0.1"]),
        ]);
        var target = new WindowContext(IntPtr.Zero, "discord", "Discord", "Friends - Discord");
        Run(new PickerForm(bw, target, (_, _) => { }) { CloseOnDeactivate = false }, snapshot);
    }

    // With a snapshot path, renders the window to a PNG and exits instead of staying open.
    static void Run(Form form, string? snapshot)
    {
        if (snapshot != null)
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(300);
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                bmp.Save(snapshot);
                form.Close();
            };
        }
        Application.Run(form);
    }
}
