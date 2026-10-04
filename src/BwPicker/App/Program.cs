using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

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
            if (args.Contains("--dark")) Palette.ForceDark = true;
            if (args.Contains("--light")) Palette.ForceDark = false;
            App.Startup = () => Preview(args);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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

        BwClient? client = null;
        CreateClient(args, ref client);
        TrayController? tray = null;
        App.Startup = () => tray = new TrayController(App.Shutdown, client);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        tray?.Dispose();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    // --preview [--dark|--light] [--unlock|--signin|--settings|--clisetup] [--query text] [--snapshot file.png]
    static void Preview(string[] args)
    {
        string? snapshot = args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault();
        string? query = args.SkipWhile(a => a != "--query").Skip(1).FirstOrDefault();
        var status = new BwStatus("locked", "you@example.com", "https://vault.example.com");
        PanelWindow window;
        if (args.Contains("--unlock")) window = new UnlockWindow(BwClient.Preview([]), status);
        else if (args.Contains("--signin")) window = new SignInWindow(BwClient.Preview([]), "vault.example.com", "you@example.com");
        else if (args.Contains("--clisetup")) window = new CliSetupWindow();
        else if (args.Contains("--settings"))
        {
            var settings = new AppSettings { LastUpdateCheck = DateTimeOffset.Now.AddHours(-3) };
            window = new SettingsWindow(BwClient.Preview([]), settings, new Updater(settings), (_, _) => { }, status);
        }
        else
        {
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
            var picker = new PickerWindow(bw, new WindowContext(IntPtr.Zero, "discord", "Discord", "Friends - Discord"), (_, _) => { })
                { CloseOnDeactivate = false };
            if (query != null) picker.Query = query == "*" ? "" : query;
            window = picker;
        }
        window.Closed += (_, _) => App.Shutdown();
        if (snapshot != null)
        {
            window.Opened += async (_, _) =>
            {
                await Task.Delay(500);
                var size = new PixelSize((int)(window.Bounds.Width * window.RenderScaling), (int)(window.Bounds.Height * window.RenderScaling));
                using var bitmap = new RenderTargetBitmap(size, new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
                bitmap.Render(window);
                using (var file = File.Create(snapshot)) bitmap.Save(file, new PngBitmapEncoderOptions());
                window.Close();
            };
        }
        window.Show();
    }
}

sealed class App : Application
{
    public static Action? Startup { get; set; }

    public static void Shutdown() =>
        Dispatcher.UIThread.Post(() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // follow the OS light/dark setting
        Ui.ApplyAccent(this);
        Name = "BwPicker";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Startup?.Invoke();
        base.OnFrameworkInitializationCompleted();
    }
}
