#if FSUIPC_CLIENT
using FSUIPC;

namespace SimDeck.Core.Sources;

/// <summary>
/// Reads LVARs in-process through the FSUIPC .NET client.
///
/// This exists because the Lua bridge hit a wall. Measured on a live Fenix:
/// a 1766ms loop, of which 562ms was reading three variables, 672ms writing a
/// one-kilobyte file, and 141ms sleeping for a requested 1ms. A sleep cannot
/// be slowed by antivirus or by WASM latency, so the only explanation is that
/// FSUIPC schedules its Lua threads a couple of times a second. That is a
/// ceiling no amount of tuning inside the loop can lift.
///
/// Here there is no Lua thread, no file, and no per-tick process boundary.
/// FSUIPC's WAPI keeps a local cache of lvar values that the WASM module
/// updates, so ReadLVar is a memory read rather than a round trip.
///
/// Needs FSUIPCClient.dll from the FSUIPC SDK - see docs/fsuipc-client.md.
/// </summary>
public sealed class FsuipcClientSource : IDataSource
{
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _values = new();
    private readonly List<string> _watch = new();
    private readonly List<string> _allLvars = new();
    private readonly Queue<DateTime> _changeTimes = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _cts;
    private volatile bool _open;
    private string? _aircraft;
    private string _lastError = "";
    private int _loopMs;
    private long _passes;

    public string DisplayName => "FSUIPC";
    public bool Connected => _open;
    public string? Aircraft { get { lock (_lock) return _aircraft; } }

    public IReadOnlyList<string> AllLvars
    {
        get { lock (_lock) return _allLvars.ToArray(); }
    }

    public int WatchCount { get { lock (_lock) return _watch.Count; } }
    public long Passes => _passes;
    public int LoopMs => _loopMs;

    public double ValueHz
    {
        get
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - RateWindow;
                while (_changeTimes.Count > 0 && _changeTimes.Peek() < cutoff)
                    _changeTimes.Dequeue();
                return _changeTimes.Count / RateWindow.TotalSeconds;
            }
        }
    }

    public SourceStatus Status
    {
        get
        {
            if (!_open)
                return new("FSUIPC: not connected",
                    _lastError.Length > 0
                        ? _lastError
                        : "Start FSUIPC7 and load a flight. It reconnects on its own.");

            return new($"FSUIPC: live · {ValueHz:0} changes/s · {WatchCount} var(s) "
                     + $"· loop {_loopMs}ms",
                       _loopMs > 50
                           ? $"Each pass takes {_loopMs}ms for {WatchCount} variable(s), "
                             + "which is slower than expected."
                           : "");
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var t = new Thread(() => Loop(_cts.Token))
        {
            IsBackground = true,
            Name = "SimDeck FSUIPC",
        };
        t.Start();
    }

    private void Loop(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            if (!_open)
            {
                try
                {
                    FSUIPCConnection.Open();
                    _open = true;
                    _lastError = "";
                    RefreshLvarList();
                }
                catch (Exception ex)
                {
                    _open = false;
                    _lastError = ex.Message;
                    if (!Sleep(ct, 2000)) return;
                    continue;
                }
            }

            var t0 = sw.ElapsedMilliseconds;
            try
            {
                string[] names;
                lock (_lock) names = _watch.ToArray();

                var fresh = new Dictionary<string, double>(names.Length);
                foreach (var n in names) fresh[n] = FSUIPCConnection.ReadLVar(n);

                // One Process call keeps the offset-backed data current; the
                // aircraft name comes from an offset, not another round trip.
                FSUIPCConnection.Process();
                var title = FSUIPCConnection.AircraftName;

                lock (_lock)
                {
                    var changed = false;
                    foreach (var kv in fresh)
                    {
                        if (!_values.TryGetValue(kv.Key, out var old)
                            || Math.Abs(old - kv.Value) > 1e-9)
                            changed = true;
                        _values[kv.Key] = kv.Value;
                    }
                    if (changed) _changeTimes.Enqueue(DateTime.UtcNow);
                    _aircraft = title;
                }
                _passes++;
            }
            catch (Exception ex)
            {
                // Sim closed under us. Drop the connection and let the loop
                // reopen rather than spinning on a dead handle.
                _open = false;
                _lastError = ex.Message;
                try { FSUIPCConnection.Close(); } catch { }
            }

            _loopMs = (int)(sw.ElapsedMilliseconds - t0);

            var remaining = 33 - _loopMs;
            if (!Sleep(ct, remaining < 1 ? 1 : remaining)) return;
        }
    }

    private static bool Sleep(CancellationToken ct, int ms)
        => !ct.WaitHandle.WaitOne(ms);

    private void RefreshLvarList()
    {
        try
        {
            var list = FSUIPCConnection.GetLvarList();
            lock (_lock)
            {
                _allLvars.Clear();
                foreach (var name in list.Keys) _allLvars.Add(name);
                _allLvars.Sort(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) { _lastError = "lvar list: " + ex.Message; }
    }

    /// <summary>Re-read the aircraft's variable list. Cheap enough to call
    /// from a button.</summary>
    public void RequestScan() => RefreshLvarList();

    public void SetWatchlist(IReadOnlyCollection<string> names)
    {
        lock (_lock) { _watch.Clear(); _watch.AddRange(names); }
    }

    public IReadOnlyDictionary<string, double> Read()
    {
        lock (_lock) return new Dictionary<string, double>(_values);
    }

    public bool TryWrite(string name, double value)
    {
        if (!_open) return false;
        try { FSUIPCConnection.WriteLVar(name, value); return true; }
        catch (Exception ex) { _lastError = ex.Message; return false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { if (_open) FSUIPCConnection.Close(); } catch { }
        _open = false;
    }
}
#endif
