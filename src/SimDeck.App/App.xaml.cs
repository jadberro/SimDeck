using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using SimDeck.App.ViewModels;
using SimDeck.Core.Sources;
using Application = System.Windows.Application;

// System.Windows and System.Windows.Forms both define MessageBox, and
// WinForms is referenced here only for NotifyIcon. Alias both names that
// collide so the intent is explicit rather than order-dependent.
using MessageBox = System.Windows.MessageBox;

// System.Drawing and System.Windows.Media both define these, and the tray
// icon is drawn with GDI+. Alias rather than fully qualifying at each use.
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace SimDeck.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    private NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private Mutex? _single;
    private System.Windows.Threading.DispatcherTimer? _trayTimer;

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimDeck");

    /// <summary>
    /// Where an unhandled failure gets written. A WPF app that throws during
    /// startup simply disappears with nothing on screen and nothing in any
    /// log, which is indistinguishable from "it does not run".
    /// </summary>
    public static string CrashLog => Path.Combine(DataDir, "crash.log");

    private void Fatal(Exception ex, string phase)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(CrashLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{phase}]{Environment.NewLine}"
                + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }

        MessageBox.Show(
            $"SimDeck hit a problem while {phase}.{Environment.NewLine}{Environment.NewLine}"
            + ex.Message + Environment.NewLine + Environment.NewLine
            + $"Full details written to:{Environment.NewLine}{CrashLog}",
            "SimDeck", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Report failures instead of vanishing. Anything that reaches here is
        // a bug, but the user should at least be told what it was.
        DispatcherUnhandledException += (_, args) =>
        {
            Fatal(args.Exception, "running");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Fatal(ex, "running");
        };

        try { Startup(e); }
        catch (Exception ex)
        {
            Fatal(ex, "starting up");
            Shutdown();
        }
    }

    private void Startup(StartupEventArgs e)
    {

        // One instance only. A second hub would bind-fail on udp/27500 and
        // leave the user with a window that silently does nothing.
        _single = new Mutex(true, "SimDeck.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("SimDeck is already running. Look for it in the " +
                            "notification area.", "SimDeck");
            Shutdown();
            return;
        }

        var profileDir = Path.Combine(DataDir, "profiles");
        var firmwareDir = Path.Combine(DataDir, "firmware");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(firmwareDir);
        SeedProfiles(profileDir);

        Settings = AppSettings.Load();

        var useMock = e.Args.Contains("--mock");

        // Both sides derive this from LOCALAPPDATA, so there is nothing to
        // configure and nothing to get out of step.
        var bridgeDir = Path.Combine(DataDir, "bridge");

        IDataSource source = useMock
            ? new MockSource()
            : SourceFactory.CreateLive(bridgeDir, e.Args.Contains("--fsuipc"));

        _vm = new MainViewModel(source, profileDir, firmwareDir);
        _vm.Start();

        BuildTray();

        _window = new MainWindow { DataContext = _vm };
        _window.Closing += OnWindowClosing;

        if (!e.Args.Contains("--minimised")) _window.Show();
    }

    /// <summary>Copy shipped profiles next to the exe into the user's data
    /// folder on first run, without ever overwriting their edits.</summary>
    private static void SeedProfiles(string target)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (!Directory.Exists(src)) return;
        foreach (var f in Directory.GetFiles(src, "*.json"))
        {
            var dest = Path.Combine(target, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest);
        }
    }

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Build(false),
            Text = "SimDeck by Jad Berro",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem("Starting…") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open SimDeck", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit SimDeck", null, (_, _) => QuitForReal());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _trayTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(2) };
        _trayTimer.Tick += (_, _) =>
        {
            if (_vm is null || _tray is null) return;
            var n = _vm.Hub.Modules.Count;
            var ok = _vm.Hub.Source.Connected;
            status.Text = $"{n} module{(n == 1 ? "" : "s")} · {(ok ? "connected" : "no data")}";
            var tip = "SimDeck by Jad Berro — " + status.Text;
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;   // NotifyIcon limit
            _tray.Icon = TrayIcon.Build(!ok);
        };
        _trayTimer.Start();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private bool _reallyQuitting;

    /// <summary>
    /// Close hides. The hub keeps streaming to the panels and the only real
    /// exit is Quit from the tray, because closing a window by reflex should
    /// never take the gauges down mid-flight.
    /// </summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyQuitting) return;

        // The user can choose to have the close button actually quit.
        if (!Settings.CloseToTray)
        {
            e.Cancel = true;
            QuitForReal();
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        _tray?.ShowBalloonTip(3000, "SimDeck is still running",
            "Your panels are still being updated. Quit from here to stop.",
            ToolTipIcon.None);
    }

    private void QuitForReal()
    {
        _reallyQuitting = true;
        _trayTimer?.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _vm?.Hub.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Tray artwork drawn at runtime, so there is no icon file to lose
/// and the alert state costs nothing to add.</summary>
internal static class TrayIcon
{
    public static Icon Build(bool alert)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var face = new SolidBrush(Color.FromArgb(255, 14, 16, 19));
            using var ring = new Pen(alert ? Color.FromArgb(255, 150, 60, 60)
                                           : Color.FromArgb(255, 95, 109, 118), 2.5f);
            using var needle = new Pen(Color.FromArgb(255, 232, 232, 230), 2.5f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round,
              EndCap = System.Drawing.Drawing2D.LineCap.Round };

            g.FillEllipse(face, 2, 2, 27, 27);
            g.DrawEllipse(ring, 2, 2, 27, 27);
            g.DrawLine(needle, 16, 16, 25, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
