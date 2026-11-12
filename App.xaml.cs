using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Piztki.Controls;
using Piztki.Core;

namespace Piztki;

public partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static Mutex? _single;
    System.Windows.Forms.NotifyIcon? _tray;
    SettingsWindow? _settingsWindow;
    TrayMenu? _trayMenu;

    public static AppSettings Settings { get; private set; } = new();
    public static NotchWindow? Notch { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SmoothScroll.Enable();
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("unhandled", args.Exception);
            args.Handled = true; // one broken button should not close the notch
        };

        // Draw on the CPU instead of spinning up a Direct3D device. For a see-through window this
        // small it measured the same frame rate with slightly less CPU and about 130 MB less memory
        // (176 MB -> 44 MB), because WPF has to copy every frame back from the GPU for transparent
        // windows anyway. PIZTKI_RENDER=gpu switches back for comparison.
        if (Environment.GetEnvironmentVariable("PIZTKI_RENDER") != "gpu")
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (e.Args.Length >= 2 && e.Args[0] == "--export-icon")
        {
            ExportIcon(e.Args[1]);
            Shutdown();
            return;
        }

        // "--discord-check file": tries the saved Discord sign-in next to the running app and writes what
        // Discord answers. Read-only: it never renews or saves anything, and shows no window.
        if (e.Args.Length >= 2 && e.Args[0] == "--discord-check")
        {
            _ = DiscordCheck(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] is "--selftest" or "--perftest")
        {
            RunSelfTest(e.Args[1], perf: e.Args[0] == "--perftest");
            return;
        }

        _single = new Mutex(true, "Piztki.SingleInstance", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        ApplyAccent();
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppSettings.Accent)) ApplyAccent();
            if (args.PropertyName == nameof(AppSettings.StartWithWindows)) ApplyStartup();
        };

        CreateTray();
        Notch = new NotchWindow();
        Notch.Show();
    }

    /// <summary>QA walkthrough on throwaway default settings; see NotchWindow.SelfTest.cs.</summary>
    async void RunSelfTest(string dir, bool perf)
    {
        Settings = new AppSettings(); // never loaded, so it never saves
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppSettings.Accent)) ApplyAccent();
        };

        var notch = new NotchWindow();
        Notch = notch;
        notch.Show();
        try
        {
            if (perf) await notch.RunPerfTestAsync(dir);
            else await notch.RunSelfTestAsync(dir);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(perf ? dir + ".error.txt" : System.IO.Path.Combine(dir, "error.txt"), ex.ToString());
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }

    public static Brush AccentBrush => (Brush)Current.Resources["AccentBrush"];

    void ApplyAccent()
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(Settings.Accent); }
        catch { color = Color.FromRgb(0xAF, 0xA9, 0xEC); }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources["AccentBrush"] = brush;
    }

    void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (Settings.StartWithWindows && Environment.ProcessPath is { } exe)
                key.SetValue("Piztki", $"\"{exe}\"");
            else
                key.DeleteValue("Piztki", throwOnMissingValue: false);
        }
        catch
        {
            // Startup registration is optional.
        }
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Piztki",
            Icon = RenderMascotIcon(),
            Visible = true,
        };

        // left click opens settings; right click opens Piztki's own menu instead of the plain Windows one
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowSettings();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowTrayMenu();
        };
    }

    LyricsWindow? _cinema;

    /// <summary>Opens the fullscreen lyrics view on the notch's screen (or brings it forward if it is open).</summary>
    public void ShowCinema()
    {
        if (_cinema != null)
        {
            _cinema.Activate();
            return;
        }
        if (Notch == null) return;

        // A whole screen of moving color is the one job that really wants the graphics card.
        // It costs memory, so it is only switched on while the cinema is open.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

        var cinema = new LyricsWindow(Notch, Notch.ChosenScreen().Bounds);
        _cinema = cinema;
        Notch.CinemaOpen = true; // the notch steps aside
        cinema.Closed += (_, _) =>
        {
            _cinema = null;
            if (Notch != null) Notch.CinemaOpen = false;
            if (Environment.GetEnvironmentVariable("PIZTKI_RENDER") != "gpu")
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        };
        cinema.Show();
        cinema.Activate();
    }

    void ShowTrayMenu()
    {
        _trayMenu?.Close();
        var menu = new TrayMenu();
        _trayMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };
        menu.Show();
        menu.Activate(); // so that clicking elsewhere deactivates and dismisses it
    }

    /// <summary>Draws the mascot into a bitmap so the tray icon always matches the character.</summary>
    public static RenderTargetBitmap RenderMascot(int size)
    {
        var mascot = new Mascot { Width = size, Height = size * 0.8, Accent = AccentBrush };
        var host = new System.Windows.Controls.Grid { Width = size, Height = size };
        host.Children.Add(mascot);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        return bmp;
    }

    /// <summary>
    /// Piztki's logo: the mascot on a lilac rounded square. The square keeps the dark cube readable
    /// on both dark and light taskbars, where the bare mascot would disappear.
    /// </summary>
    public static BitmapSource RenderAppIcon(int size)
    {
        var background = new System.Windows.Controls.Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.23),
            Background = new LinearGradientBrush(Color.FromRgb(0xCF, 0xCA, 0xF8), Color.FromRgb(0x8A, 0x82, 0xE2), 55),
        };
        var mascot = new Mascot
        {
            Width = size * 0.96,
            Height = size * 0.96 * 0.8,
            Accent = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, size * 0.02, 0, 0),
        };

        var host = new System.Windows.Controls.Grid { Width = size, Height = size };
        host.Children.Add(background);
        host.Children.Add(mascot);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        return bmp;
    }

    static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static System.Drawing.Icon RenderMascotIcon()
    {
        using var ms = new MemoryStream(Png(RenderAppIcon(64)));
        using var bitmap = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// "Piztki.exe --export-icon file.ico": writes the logo as a Windows icon file (several sizes),
    /// which the build then stamps onto Piztki.exe. Also saves a 256px PNG next to it.
    /// </summary>
    /// <summary>Appends an error to %LocalAppData%\Piztki\errors.log (kept small), for looking at later.</summary>
    public static void LogError(string where, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piztki");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "errors.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where}\n{ex}\n\n");
        }
        catch
        {
            // logging is best-effort
        }
    }

    async Task DiscordCheck(string file)
    {
        var log = new List<string>();
        try
        {
            var s = AppSettings.Load();
            log.Add($"saved: id={s.DiscordAppId.Length} chars, secret={s.DiscordSecret.Length}, access={s.DiscordAccess.Length}, refresh={s.DiscordRefresh.Length}, presence={s.DiscordPresence}, voice={s.DiscordVoice}");
            using var rpc = new DiscordRpc();
            bool connected = await rpc.ConnectAsync(s.DiscordAppId);
            log.Add($"connect: {connected} user='{rpc.UserName}' status='{rpc.Status}'");
            if (connected)
            {
                // the access token only: no renewing, which would spoil the running app's saved pair
                var tokens = await rpc.SignInForVoiceAsync("", s.DiscordAccess, "", mayAsk: false);
                log.Add($"voice sign-in with the saved token: {(tokens != null ? "accepted" : "refused")}  discord said='{rpc.LastSignInError}'  status='{rpc.Status}'");
                log.Add($"voice ready={rpc.VoiceReady} channel='{rpc.Channel}' channelId={rpc.ChannelId.Length} chars members={rpc.MemberCount} muted={rpc.Muted} deafened={rpc.Deafened}");
            }
        }
        catch (Exception ex)
        {
            log.Add("failed: " + ex);
        }
        File.WriteAllLines(file, log);
        Shutdown();
    }

    static void ExportIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var images = sizes.Select(s => Png(RenderAppIcon(s))).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = new BinaryWriter(File.Create(path)))
        {
            file.Write((short)0);            // reserved
            file.Write((short)1);            // type: icon
            file.Write((short)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); // 0 means 256
                file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                file.Write((byte)0);         // no palette
                file.Write((byte)0);         // reserved
                file.Write((short)1);        // color planes
                file.Write((short)32);       // bits per pixel
                file.Write(images[i].Length);
                file.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) file.Write(image);
        }

        File.WriteAllBytes(Path.ChangeExtension(path, ".png"), images[^1]);
        // a big one for places that want it: store listings, the Discord application's icon
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-1024.png"), Png(RenderAppIcon(1024)));
    }
}
