using System.Collections.ObjectModel;
using System.Windows.Threading;
using Microsoft.Win32;
using SimDeck.Core;
using SimDeck.Core.Ota;
using SimDeck.Core.Sources;
using System.IO;

namespace SimDeck.App.ViewModels;

public enum Page { Devices, DeviceDetail, Variables, Firmware, Settings }

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private Page _page = Page.Devices;
    private DeviceViewModel? _selected;
    private string _sourceLine = "starting";
    private string _aircraftLine = "no aircraft";
    private string _profileLine = "no profile";
    private bool _sourceOk;

    public HubService Hub { get; }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<FirmwareViewModel> Firmware { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public VariablesViewModel Vars { get; }

    // ---- preferences -------------------------------------------------------

    public bool LaunchAtStartup
    {
        get => App.Settings.LaunchAtStartup;
        set
        {
            if (App.Settings.LaunchAtStartup == value) return;
            App.Settings.LaunchAtStartup = value;
            App.Settings.Save();
            Raise();
        }
    }

    public bool CloseToTray
    {
        get => App.Settings.CloseToTray;
        set
        {
            if (App.Settings.CloseToTray == value) return;
            App.Settings.CloseToTray = value;
            App.Settings.Save();
            Raise();
            Raise(nameof(CloseBehaviourHint));
        }
    }

    public string CloseBehaviourHint => CloseToTray
        ? "Closing the window keeps SimDeck running in the notification area, so your panels stay live. Quit from the tray icon."
        : "Closing the window quits SimDeck. Your panels will stop updating.";

    private string _bridgeLine = "";
    private string _bridgeHint = "";

    /// <summary>One line describing the state of the FSUIPC bridge.</summary>
    public string BridgeLine { get => _bridgeLine; set => Set(ref _bridgeLine, value); }

    /// <summary>What to do about it, when something is wrong.</summary>
    public string BridgeHint { get => _bridgeHint; set => Set(ref _bridgeHint, value); }

    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(IsDevices)); Raise(nameof(IsDetail));
            Raise(nameof(IsVariables)); Raise(nameof(IsFirmware));
            Raise(nameof(IsSettings));
        }
    }

    public bool IsDevices   => _page is Page.Devices;
    public bool IsDetail    => _page is Page.DeviceDetail;
    public bool IsVariables => _page is Page.Variables;
    public bool IsFirmware  => _page is Page.Firmware;
    public bool IsSettings  => _page is Page.Settings;

    public DeviceViewModel? Selected { get => _selected; set => Set(ref _selected, value); }

    public string SourceLine    { get => _sourceLine; set => Set(ref _sourceLine, value); }
    public string AircraftLine  { get => _aircraftLine; set => Set(ref _aircraftLine, value); }
    public string ProfileLine   { get => _profileLine; set => Set(ref _profileLine, value); }
    public bool SourceOk        { get => _sourceOk; set => Set(ref _sourceOk, value); }

    public RelayCommand OpenDevice { get; }
    public RelayCommand Back { get; }
    public RelayCommand GoDevices { get; }
    public RelayCommand GoVariables { get; }
    public RelayCommand GoFirmware { get; }
    public RelayCommand GoSettings { get; }
    public RelayCommand Identify { get; }
    public RelayCommand PushFirmware { get; }
    public RelayCommand AddImage { get; }
    public RelayCommand UpdateAllOfType { get; }
    public RelayCommand ReloadProfiles { get; }

    public MainViewModel(IDataSource source, string profileDir, string firmwareDir)
    {
        Hub = new HubService(source, profileDir, firmwareDir);
        Vars = new VariablesViewModel(Hub);

        Hub.Log += (_, e) => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Log.Insert(0, e.Message);
            while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
        });

        OpenDevice = new RelayCommand(p =>
        {
            if (p is DeviceViewModel d) { Selected = d; CurrentPage = Page.DeviceDetail; }
        });
        Back = new RelayCommand(_ => CurrentPage = Page.Devices);
        GoDevices = new RelayCommand(_ => CurrentPage = Page.Devices);
        GoVariables = new RelayCommand(_ => CurrentPage = Page.Variables);
        GoFirmware = new RelayCommand(_ => CurrentPage = Page.Firmware);
        GoSettings = new RelayCommand(_ => CurrentPage = Page.Settings);

        Identify = new RelayCommand(_ => { if (Selected is not null) Hub.Identify(Selected.Id); });

        PushFirmware = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            var (ok, err) = Hub.ForceOta(Selected.Id);
            if (!ok) System.Windows.MessageBox.Show(err, "SimDeck");
        });

        UpdateAllOfType = new RelayCommand(p =>
        {
            if (p is FirmwareViewModel f)
            {
                var n = Hub.UpdateAllOfType(f.ModuleType);
                if (n == 0) System.Windows.MessageBox.Show(
                    "Nothing to update — every connected device of that type is already current.",
                    "SimDeck");
            }
        });

        AddImage = new RelayCommand(_ => AddImageDialog());
        ReloadProfiles = new RelayCommand(_ => Hub.ReloadProfiles());

        // Fast enough that live numbers do not look artificially steppy, slow
        // enough not to compete with the tick loop for CPU. The gauge does not
        // depend on this - it samples per frame.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Hub.Start();
        _timer.Start();
    }

    /// <summary>
    /// Accepts what the Arduino IDE actually produces.
    ///
    /// "Export compiled binary" writes a folder containing
    /// accu_panel.ino.bin plus .merged.bin, .bootloader.bin and
    /// .partitions.bin. Only the first is a valid OTA image: merged images
    /// start with the bootloader at offset 0 and will fail to boot if
    /// written to an app partition.
    /// </summary>
    private void AddImageDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a compiled firmware image",
            Filter = "Firmware image (*.bin)|*.bin",
        };
        if (dlg.ShowDialog() != true) return;

        var file = Path.GetFileName(dlg.FileName);

        if (!FirmwareImageName.TryParse(file, out var type, out var version, out var reject))
        {
            System.Windows.MessageBox.Show(reject, "SimDeck",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Publish this image?\n\n" +
            $"Module type:  {type}\n" +
            $"Version:      {version}\n\n" +
            "Any connected panel of that type running an older version will " +
            "be offered the update.",
            "SimDeck",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.OK) return;

        Hub.Repo.Publish(type, dlg.FileName, version, notes: $"added from {file}");
        Refresh();
    }

    private void Refresh()
    {
        SourceOk = Hub.Source.Connected;
        SourceLine = $"{Hub.Source.DisplayName} · {(SourceOk ? "live" : "no data")}";
        AircraftLine = Hub.Source.Aircraft ?? "no aircraft";
        ProfileLine = Hub.Profile?.Name ?? "no profile";

        UpdateBridgeStatus();
        Vars.Refresh();

        var manifest = Hub.Repo.All();
        var live = Hub.Modules;

        foreach (var m in live)
        {
            var vm = Devices.FirstOrDefault(d => d.Id == m.Id);
            if (vm is null)
            {
                vm = new DeviceViewModel { Id = m.Id };
                Devices.Add(vm);
            }
            manifest.TryGetValue(m.ModuleType, out var entry);
            vm.Update(m, Hub.Ota.Get(m.Id), entry);
        }

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (live.Any(m => m.Id == Devices[i].Id)) continue;
            if (ReferenceEquals(Selected, Devices[i]))
            {
                // Do not yank the page out from under someone reading it.
                Devices[i].StatusColour = "#D9534F";
                Devices[i].Subtitle = "offline";
                continue;
            }
            Devices.RemoveAt(i);
        }

        RefreshFirmware(manifest, live);
    }

    /// <summary>
    /// Show whatever the source says about itself, then add the one thing it
    /// cannot know: whether an aircraft profile matched.
    ///
    /// This used to type-test for the Lua bridge, which meant any other source
    /// fell through to a message about mock mode that made no sense.
    /// </summary>
    private void UpdateBridgeStatus()
    {
        var status = Hub.Source.Status;
        BridgeLine = status.Line;

        if (status.Hint.Length > 0) { BridgeHint = status.Hint; return; }

        BridgeHint = Profile is null
            ? $"Connected, but no aircraft profile matches '{Hub.Source.Aircraft}'. "
              + "Until one does, nothing is polled."
            : "";
    }

    private AircraftProfile? Profile => Hub.Profile;

    private void RefreshFirmware(Dictionary<string, FirmwareEntry> manifest,
                                 IReadOnlyList<ModuleInfo> live)
    {
        foreach (var (type, entry) in manifest)
        {
            var vm = Firmware.FirstOrDefault(f => f.ModuleType == type);
            if (vm is null)
            {
                vm = new FirmwareViewModel { ModuleType = type };
                Firmware.Add(vm);
            }

            var of = live.Where(m => m.ModuleType == type).ToList();
            var behind = of.Count(m => OtaTracker.IsNewer(entry.Version, m.Firmware));

            vm.VersionLine = entry.Version;
            vm.Behind = behind > 0;
            vm.DeviceLine = of.Count == 0
                ? "no devices connected"
                : behind == 0
                    ? $"{of.Count} device{(of.Count == 1 ? "" : "s")} up to date"
                    : $"{behind} of {of.Count} behind";
        }

        for (int i = Firmware.Count - 1; i >= 0; i--)
            if (!manifest.ContainsKey(Firmware[i].ModuleType))
                Firmware.RemoveAt(i);
    }
}
