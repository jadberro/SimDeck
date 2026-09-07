using System.Windows;
using System.Windows.Media;
using SimDeck.Bench.Network;

namespace SimDeck.Bench;

public partial class MainWindow : Window
{
    private readonly BenchPanelClient _client;
    private double _accum;
    private double _left;
    private double _right;
    private bool _live;
    private bool _parkBrakeOn;
    private DateTime _identifyUntil;

    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xE7, 0x4C, 0x3C));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xF3, 0x9C, 0x12));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly SolidColorBrush WhiteBrush = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    public MainWindow()
    {
        InitializeComponent();

        _client = new BenchPanelClient();

        // Feed the gauge control on each rendering frame
        Gauge.Sample = () => (_accum, _left, _right, _live);

        _client.ValuesReceived += OnValuesReceived;
        _client.StatusChanged += OnStatusChanged;
        _client.LogMessage += OnLogMessage;
        _client.Identify += OnIdentify;
        _client.OtaProgress += OnOtaProgress;

        TxtFw.Text = $"v{_client.CurrentFw}";
    }

    private void OnValuesReceived((double accum, double left, double right, bool live) vals)
    {
        _accum = vals.accum;
        _left = vals.left;
        _right = vals.right;
        _live = vals.live;

        Dispatcher.InvokeAsync(() =>
        {
            TxtAccum.Text = $"{_accum:N0} psi";
            TxtLeft.Text = $"{_left:N0} psi";
            TxtRight.Text = $"{_right:N0} psi";
            TxtRate.Text = $"{_client.FrameRate:0.0} Hz";
            TxtSeq.Text = $"{_client.LastSeq}";

            UpdateStatusDisplay();
        });
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.InvokeAsync(UpdateStatusDisplay);
    }

    private void UpdateStatusDisplay()
    {
        if (DateTime.UtcNow < _identifyUntil)
        {
            StatusDot.Fill = WhiteBrush;
            StatusText.Text = "IDENTIFY FLASH";
            return;
        }

        if (_live)
        {
            StatusDot.Fill = GreenBrush;
            StatusText.Text = "Live Streaming";
        }
        else if (_client.IsLinked)
        {
            StatusDot.Fill = AmberBrush;
            StatusText.Text = "Linked (Sim Waiting)";
        }
        else
        {
            StatusDot.Fill = RedBrush;
            StatusText.Text = "Discovering Hub...";
        }
    }

    private void OnIdentify()
    {
        _identifyUntil = DateTime.UtcNow.AddSeconds(4);
        Dispatcher.InvokeAsync(() =>
        {
            UpdateStatusDisplay();
            Task.Delay(4000).ContinueWith(_ => Dispatcher.InvokeAsync(UpdateStatusDisplay));
        });
    }

    private void OnOtaProgress(string state, int pct, string? err)
    {
        Dispatcher.InvokeAsync(() =>
        {
            switch (state)
            {
                case "start":
                    OtaBar.Value = 0;
                    TxtOtaStatus.Text = "Starting download...";
                    break;
                case "progress":
                    OtaBar.Value = pct;
                    TxtOtaStatus.Text = $"Downloading {pct}%";
                    break;
                case "ok":
                    OtaBar.Value = 100;
                    TxtOtaStatus.Text = "Upgraded & Verified!";
                    TxtFw.Text = $"v{_client.CurrentFw}";
                    break;
                case "fail":
                    TxtOtaStatus.Text = $"Failed: {err ?? "unknown error"}";
                    break;
            }
        });
    }

    private void OnLogMessage(string msg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
            LogList.Items.Add(line);
            if (LogList.Items.Count > 100)
                LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(line);
        });
    }

    private void BtnParkBrake_Click(object sender, RoutedEventArgs e)
    {
        _parkBrakeOn = !_parkBrakeOn;
        _client.SendInput("park_brake", _parkBrakeOn ? 1.0 : 0.0);
        BtnParkBrake.Content = _parkBrakeOn ? "Release Park Brake" : "Set Park Brake";
    }

    private void BtnLeftPedal_Click(object sender, RoutedEventArgs e)
    {
        _client.SendInput("brake_left", 1.0);
        Task.Delay(400).ContinueWith(_ => _client.SendInput("brake_left", 0.0));
    }

    private void BtnRightPedal_Click(object sender, RoutedEventArgs e)
    {
        _client.SendInput("brake_right", 1.0);
        Task.Delay(400).ContinueWith(_ => _client.SendInput("brake_right", 0.0));
    }

    protected override void OnClosed(EventArgs e)
    {
        _client.Dispose();
        base.OnClosed(e);
    }
}
