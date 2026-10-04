namespace BwPicker;

static partial class Program
{
    /// <summary>Optional local extension point (e.g. an untracked *.local.cs); compiled out when not implemented.</summary>
    static partial void CreateClient(string[] args, ref BwClient? client);

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--preview"))
        {
            ApplicationConfiguration.Initialize();
            if (args.Contains("--dark")) Theme.ForceDark = true;
            if (args.Contains("--light")) Theme.ForceDark = false;
            string? snapshot = args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault();
            if (args.Contains("--settings")) ShowSettingsPreview(snapshot);
            else if (args.Contains("--clisetup")) Run(() => new CliSetupForm(), snapshot);
            else if (args.Contains("--signin")) Run(() => new SignInForm(BwClient.Preview([]), "vault.example.com", "you@example.com"), snapshot);
            else ShowPreview(args.Contains("--unlock"), snapshot, args.SkipWhile(a => a != "--query").Skip(1).FirstOrDefault());
            return;
        }

        // After a self-update, wait for the previous instance to exit before taking the single-instance lock.
        Updater.FinishUpdate(args);
        using var mutex = new Mutex(true, "BwPicker.SingleInstance", out bool first);
        if (!first) return;

        try
        {
            // Install-Admin.ps1 starts the Program Files copy with this flag to set up admin autostart.
            if (args.Contains("--enable-admin-autostart") && Startup.AdminMode) Startup.Enabled = true;
            Startup.Refresh();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { }
        ApplicationConfiguration.Initialize();
        BwClient? client = null;
        CreateClient(args, ref client);
        Application.Run(new TrayApp(client));
    }

    static void ShowSettingsPreview(string? snapshot)
    {
        var settings = new AppSettings { LastUpdateCheck = DateTimeOffset.Now.AddHours(-3) };
        var status = new BwStatus("locked", "you@example.com", "https://vault.example.com");
        Run(() => new SettingsForm(BwClient.Preview([]), settings, new Updater(settings), (_, _) => { }, status), snapshot);
    }

    static void ShowPreview(bool unlock, string? snapshot, string? query)
    {
        if (unlock)
        {
            Run(() => new UnlockForm(BwClient.Preview([]), new BwStatus("locked", "you@example.com", "https://vault.example.com")), snapshot);
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
        Run(() =>
        {
            var picker = new PickerForm(bw, target, (_, _) => { }) { CloseOnDeactivate = false };
            if (query != null) picker.Controls.OfType<TextBox>().Single().Text = query == "*" ? "" : query;
            return picker;
        }, snapshot);
    }

    // With a snapshot path, renders the window to a PNG and exits instead of staying open.
    static void Run(Func<Form> createForm, string? snapshot)
    {
        // Match the tray app: create the popup after the message loop has started.
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var form = createForm();
            form.FormClosed += (_, _) => { form.Dispose(); context.ExitThread(); };
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
            form.Show();
        };
        timer.Start();
        Application.Run(context);
    }
}
