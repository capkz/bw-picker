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
        // The updater checks a download's version this way before installing it.
        if (args is ["--version"]) { Console.WriteLine(AppVersion.Text); return; }

#if LINUX
        // Xlib must be told about threads before any connection opens (the hotkey thread has its own).
        if (X11.Available) X11.XInitThreads();
        // `--pick` (bound to a desktop shortcut on Wayland) asks the running instance to open the picker.
        if (args.Contains("--pick") && GlobalHotkey.SendPick()) return;
#endif

        // Before any UI code loads SkiaSharp/HarfBuzz/ANGLE.
        if (!NativeLibraries.Ensure()) return;

        if (args.Contains("--preview"))
        {
            if (args.Contains("--dark")) Palette.ForceDark = true;
            if (args.Contains("--light")) Palette.ForceDark = false;
            App.Startup = () => Preview(args);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            return;
        }

#if WINDOWS
        // Elevated helper started by the "Run as administrator" setting: install into Program Files and hand over.
        if (args.Contains("--install-admin")) { AdminInstall.RunHelper(args); return; }
#endif

        // After a self-update, wait for the previous instance to exit before taking the single-instance lock.
        Updater.FinishUpdate(args);
        using var mutex = AcquireSingleInstance();
        if (mutex == null) return;

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

    /// <summary>
    /// One BwPicker per session. When switching between normal and administrator mode the previous instance is still
    /// exiting, so wait up to 10 seconds for it. An administrator instance's mutex can't even be opened by a normal one
    /// (access denied), which also means "still running".
    /// </summary>
    static Mutex? AcquireSingleInstance()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                var mutex = new Mutex(true, "BwPicker.SingleInstance", out bool created);
                if (created) return mutex;
                try
                {
                    if (mutex.WaitOne(deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero)) return mutex;
                }
                catch (AbandonedMutexException) { return mutex; }
                mutex.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                Thread.Sleep(250);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // Development builds log Avalonia warnings and unhandled errors to %TEMP%\BwPicker-debug.log (no vault data).
        if (AppVersion.IsDevelopment)
        {
            var log = new System.Diagnostics.TextWriterTraceListener(Path.Combine(Path.GetTempPath(), "BwPicker-debug.log"));
            System.Diagnostics.Trace.Listeners.Add(log);
            System.Diagnostics.Trace.AutoFlush = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => System.Diagnostics.Trace.WriteLine($"Unhandled: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) => System.Diagnostics.Trace.WriteLine($"Unobserved: {e.Exception}");
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
            .With(new MacOSPlatformOptions { ShowInDock = false }); // a menu-bar app on macOS (ignored elsewhere)
    }

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
