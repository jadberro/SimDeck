using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using SimDeck.Core;
using SimDeck.Core.Sources;

namespace SimDeck.App.ViewModels;

public sealed class LvarRow : ObservableObject
{
    private string _value = "—";
    private bool _probed;

    public required string Name { get; init; }
    public required Action<LvarRow> OnProbeChanged { get; init; }

    public string Value { get => _value; set => Set(ref _value, value); }

    public bool Probed
    {
        get => _probed;
        set { if (Set(ref _probed, value)) OnProbeChanged(this); }
    }
}

/// <summary>
/// Finds variables. This is the page that replaces reading
/// Cockpit_Behavior.xml by hand: scan the aircraft, filter by name, tick the
/// candidates, and watch which ones move when you set the parking brake.
/// </summary>
public sealed class VariablesViewModel : ObservableObject
{
    private readonly HubService _hub;
    private readonly List<LvarRow> _all = new();
    private string _filter = "";
    private string _status = "";
    private bool _busy;

    public ObservableCollection<LvarRow> Rows { get; } = new();

    public RelayCommand Scan { get; }
    public RelayCommand ImportFromAircraft { get; }
    public RelayCommand BrowseForFile { get; }
    public RelayCommand AddManual { get; }
    public RelayCommand ClearProbes { get; }

    private string _manualName = "";
    public string ManualName { get => _manualName; set => Set(ref _manualName, value); }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) ApplyFilter(); }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    public VariablesViewModel(HubService hub)
    {
        _hub = hub;

        Scan = new RelayCommand(_ =>
        {
            if (!_hub.Source.Connected)
            {
                Status = "The source is not connected. Fix that first.";
                return;
            }

            switch (_hub.Source)
            {
                case FsuipcLuaSource lua:
                    Busy = true;
                    Status = "Scanning…";
                    lua.RequestScan();
                    break;
                case SimConnectSource:
                    // SimConnect can read any variable you name but has no
                    // call to list them, so there is nothing to scan.
                    Status = "SimConnect cannot list variables - there is no such "
                           + "call. Use 'Read from aircraft files', which is more "
                           + "reliable anyway.";
                    break;

                default:
                    Status = "This source cannot list variables. Use "
                           + "'Read from aircraft files' instead.";
                    break;
            }
        });

        // The Lua scan needs ipc.getLvarList, which some FSUIPC builds do not
        // have - and then it returns nothing with no explanation. The
        // aircraft's own files are always readable and need no FSUIPC at all.
        ImportFromAircraft = new RelayCommand(_ =>
        {
            Busy = true;
            Status = "Searching the simulator's package folders…";

            Task.Run(() =>
            {
                var files = LvarCatalog.FindBehaviourFiles("fnx");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    try { foreach (var n in LvarCatalog.FromFile(f)) names.Add(n); }
                    catch (IOException) { }
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    Busy = false;
                    if (names.Count == 0)
                    {
                        Status = files.Count == 0
                            ? "No Fenix package found automatically. Use Browse "
                              + "and pick Cockpit_Behavior.xml from the Fenix folder."
                            : $"Read {files.Count} file(s) but found no variables.";
                        return;
                    }
                    if (_hub.Source is SimConnectSource sc) sc.SetKnownNames(names);
                    Load(names, $"{names.Count} variables from {files.Count} aircraft file(s).");
                });
            });
        });

        BrowseForFile = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Pick the aircraft's behaviour file",
                Filter = "Behaviour XML (*.xml)|*.xml",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var names = LvarCatalog.FromFile(dlg.FileName);
                if (names.Count == 0) { Status = "No variables in that file."; return; }
                Load(names, $"{names.Count} variables from {Path.GetFileName(dlg.FileName)}.");
            }
            catch (IOException ex) { Status = ex.Message; }
        });

        // Sometimes you already know, or half know, the name.
        AddManual = new RelayCommand(_ =>
        {
            var n = ManualName.Trim();
            if (n.Length == 0) return;
            if (n.StartsWith("L:", StringComparison.OrdinalIgnoreCase)) n = n[2..].Trim();

            if (!_all.Any(r => r.Name == n))
                _all.Add(new LvarRow { Name = n, OnProbeChanged = OnProbeChanged });

            var row = _all.First(r => r.Name == n);
            row.Probed = true;
            ManualName = "";
            Status = $"Watching {n}. It will read 'waiting' if the name does not exist.";
            ApplyFilter();
        });

        ClearProbes = new RelayCommand(_ =>
        {
            _hub.ClearProbes();
            foreach (var r in _all) r.Probed = false;
        });

        if (_hub.Source is FsuipcLuaSource src)
        {
            src.ScanCompleted += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.Invoke(LoadScan);
        }
    }

    private void LoadScan()
    {
        Busy = false;
        if (_hub.Source is not FsuipcLuaSource lua) return;

        var names = lua.AllLvars;
        if (names.Count == 0)
        {
            Status = "The scan returned nothing. Your FSUIPC build may not "
                   + "expose ipc.getLvarList — check the FSUIPC log.";
            return;
        }

        Load(names, $"{names.Count} variables from the simulator.");
    }

    /// <summary>
    /// Replace the list, keeping anything currently ticked so a second import
    /// does not throw away what you were already watching.
    /// </summary>
    private void Load(IEnumerable<string> names, string status)
    {
        var probed = new HashSet<string>(_hub.Probes, StringComparer.Ordinal);
        var keep = _all.Where(r => r.Probed).ToList();

        _all.Clear();
        foreach (var n in names.Distinct(StringComparer.Ordinal)
                               .OrderBy(n => n, StringComparer.Ordinal))
        {
            _all.Add(new LvarRow
            {
                Name = n,
                Probed = probed.Contains(n),
                OnProbeChanged = OnProbeChanged,
            });
        }
        foreach (var k in keep)
            if (!_all.Any(r => r.Name == k.Name)) _all.Add(k);

        // Put the likely candidates in front so the filter box is optional.
        var ranked = LvarCatalog.Rank(_all.Select(r => r.Name),
                                      "BRAKE", "ACCU", "PRESS");
        if (ranked.Count > 0)
        {
            var order = ranked.Select((n, i) => (n, i))
                              .ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
            _all.Sort((a, b) =>
            {
                var ia = order.TryGetValue(a.Name, out var x) ? x : int.MaxValue;
                var ib = order.TryGetValue(b.Name, out var y) ? y : int.MaxValue;
                return ia != ib ? ia.CompareTo(ib)
                                : string.CompareOrdinal(a.Name, b.Name);
            });
        }

        Status = status + " Brake and accumulator candidates are listed first. "
               + "Tick a few and watch which move when you set the parking brake.";
        ApplyFilter();
    }

    private void OnProbeChanged(LvarRow row)
    {
        if (row.Probed) _hub.AddProbe(row.Name);
        else _hub.RemoveProbe(row.Name);
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var f = _filter.Trim();

        // Ticked rows stay visible whatever the filter says, so you never
        // lose track of what you are watching mid-search.
        var shown = _all.Where(r =>
            r.Probed || f.Length == 0 ||
            r.Name.Contains(f, StringComparison.OrdinalIgnoreCase));

        foreach (var r in shown.Take(400)) Rows.Add(r);

        if (_all.Count > 0 && Rows.Count == 400)
            Status = "Showing the first 400 matches. Narrow the filter.";
    }

    /// <summary>Called on the UI tick to refresh live values.</summary>
    public void Refresh()
    {
        var snap = _hub.Snapshot;
        foreach (var r in Rows)
        {
            if (!r.Probed) { r.Value = "—"; continue; }
            r.Value = snap.TryGetValue(r.Name, out var v)
                ? v.ToString("0.####")
                : "waiting";
        }
    }
}
