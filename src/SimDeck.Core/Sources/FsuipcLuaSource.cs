using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimDeck.Core.Sources;

/// <summary>
/// Reads LVARs through a Lua script running inside FSUIPC7.
///
/// The transport is a FILE, not a socket. FSUIPC's Lua com library is thinly
/// documented and an earlier version used socket calls that may not exist in
/// every build: the script failed on load and SimDeck sat at "no data" with
/// nothing to explain why. io.open and ipc.readLvar certainly exist.
///
/// The file is also far easier to diagnose - open it and you can see whether
/// values are being written at all. UDP is still accepted as a low-latency
/// extra, but nothing depends on it.
///
/// Both sides derive the folder from LOCALAPPDATA, so there is nothing to
/// configure and nothing to fall out of step.
/// </summary>
public sealed class FsuipcLuaSource : IDataSource
{
    public const int RxPort = 27510;   // optional UDP fast path

    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);

    private readonly string _bridgeDir;
    private readonly string _statusPath;
    private readonly string _watchlistPath;
    private readonly string _commandPath;
    private readonly string _scanPath;

    private readonly object _lock = new();
    private readonly Dictionary<string, double> _values = new();
    private readonly List<string> _allLvars = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private DateTime _lastRx = DateTime.MinValue;
    private string? _aircraft;
    private int _watchCount;
    private int _lastSeq = -1;
    private long _heartbeats;
    private string _scriptError = "";
    private int _msTotal, _msRead, _msIo;
    private string _clockKind = "";
    private int _scriptVersion;
    private int _msSleep;

    /// <summary>Version reported by the running script, 0 if it predates
    /// versioning. Compare with ExpectedScriptVersion.</summary>
    public int ScriptVersion { get { lock (_lock) return _scriptVersion; } }

    /// <summary>Milliseconds the script spends asleep per pass.</summary>
    public int SleepMs { get { lock (_lock) return _msSleep; } }

    /// <summary>What ships in this build.</summary>
    public const int ExpectedScriptVersion = 4;

    /// <summary>"real" or "cpu". CPU timings understate blocking calls, so it
    /// matters when reading the numbers.</summary>
    public string ClockKind { get { lock (_lock) return _clockKind; } }

    /// <summary>Milliseconds the script spends per iteration, and where.</summary>
    public int LoopMs { get { lock (_lock) return _msTotal; } }
    public int ReadMs { get { lock (_lock) return _msRead; } }
    public int WriteMs { get { lock (_lock) return _msIo; } }

    // Two separate rates, because they fail for different reasons and the
    // difference between them is the whole diagnosis:
    //   beats  - how often the Lua script writes  (our side)
    //   values - how often a number actually changes (FSUIPC's side)
    private readonly Queue<DateTime> _beatTimes = new();
    private readonly Queue<DateTime> _changeTimes = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    /// <summary>Status writes per second from the Lua script.</summary>
    public double BeatHz { get { lock (_lock) return Rate(_beatTimes); } }

    /// <summary>
    /// Value changes per second. If this is far below BeatHz, the bridge is
    /// fine and the simulator side is the bottleneck - see LvarUpdateFrequency
    /// in FSUIPC_WASM.ini.
    /// </summary>
    public double ValueHz { get { lock (_lock) return Rate(_changeTimes); } }

    private static double Rate(Queue<DateTime> q)
    {
        var cutoff = DateTime.UtcNow - RateWindow;
        while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
        return q.Count / RateWindow.TotalSeconds;
    }

    /// <summary>Last error reported by the Lua side, or empty. The script
    /// carries this in the status file so a failure is visible here rather
    /// than only in the FSUIPC log.</summary>
    public string ScriptError { get { lock (_lock) return _scriptError; } }

    public FsuipcLuaSource(string bridgeDir)
    {
        _bridgeDir     = bridgeDir;
        _statusPath    = Path.Combine(bridgeDir, "status.txt");
        _watchlistPath = Path.Combine(bridgeDir, "lvars.txt");
        _commandPath   = Path.Combine(bridgeDir, "command.txt");
        _scanPath      = Path.Combine(bridgeDir, "scan.txt");
        Directory.CreateDirectory(bridgeDir);
    }

    public string DisplayName => "FSUIPC (Lua bridge)";

    public SourceStatus Status
    {
        get
        {
            if (Heartbeats == 0)
                return new("bridge: nothing received",
                    $"No status file in {BridgeDirectory}. Check FSUIPC7 is "
                    + "running and connected, that a flight is loaded, and that "
                    + "simdeck.lua is installed. The FSUIPC log will say "
                    + "'SimDeck lua started'.");

            if (ScriptVersion < ExpectedScriptVersion)
                return new($"bridge: script v{ScriptVersion} (expected v{ExpectedScriptVersion})",
                    "Run tools\\install-lua.cmd with FSUIPC7 closed, then restart it.");

            if (!Connected)
                return new($"bridge: stalled after {Heartbeats} update(s)",
                    ScriptError.Length > 0 ? $"The script reported: {ScriptError}"
                                           : "The sim or FSUIPC has closed.");

            var line = $"bridge: live · {BeatHz:0} writes/s · {ValueHz:0} changes/s "
                     + $"· {WatchCount} var(s) · loop {LoopMs}ms";

            // Every category slow at once, including the sleep, means the Lua
            // thread is not being scheduled - not disk, not antivirus, not the
            // WASM. That is a ceiling this transport cannot get past.
            if (LoopMs > 400 && SleepMs > 50)
                return new(line,
                    $"A 1ms sleep is taking {SleepMs}ms and every operation is "
                    + "slow, so FSUIPC is not scheduling the Lua thread often "
                    + "enough. No tuning here will fix that - switch to the "
                    + "FSUIPC .NET client (see docs/fsuipc-client.md).");

            if (LoopMs > 150)
                return new(line,
                    $"Each pass takes {LoopMs}ms (read {ReadMs}, write {WriteMs}, "
                    + $"sleep {SleepMs}).");

            return new(line, "");
        }
    }

    /// <summary>Where both sides meet. Shown in the UI so it can be checked.</summary>
    public string BridgeDirectory => _bridgeDir;

    public bool Connected
    {
        get { lock (_lock) return DateTime.UtcNow - _lastRx < Stale; }
    }

    public string? Aircraft { get { lock (_lock) return _aircraft; } }

    /// <summary>How many names the Lua side is currently polling.</summary>
    public int WatchCount { get { lock (_lock) return _watchCount; } }

    /// <summary>Status updates seen. Non-zero proves the script is running.</summary>
    public long Heartbeats { get { lock (_lock) return _heartbeats; } }

    /// <summary>Every LVAR the aircraft exposes, from the last scan.</summary>
    public IReadOnlyList<string> AllLvars
    {
        get { lock (_lock) return _allLvars.ToArray(); }
    }

    public bool ScanInProgress { get; private set; }
    public event EventHandler? ScanCompleted;

    // -- lifecycle ----------------------------------------------------------

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => FileLoop(_cts.Token));

        // UDP is a bonus. If the port is busy, or the Lua side has no socket
        // support, the file loop carries everything regardless.
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, RxPort));
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (SocketException) { _udp = null; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
    }

    // -- file transport -----------------------------------------------------

    private async Task FileLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { ReadStatusFile(); }
            catch (IOException) { }              // mid-write; retry next tick
            catch (UnauthorizedAccessException) { }

            try { await Task.Delay(15, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Reads the status file the Lua script rewrites each tick.
    ///
    /// A file without the trailing #END was caught mid-write, so it is
    /// discarded rather than parsed - half a file would otherwise look like a
    /// set of variables that had suddenly disappeared.
    /// </summary>
    private void ReadStatusFile()
    {
        if (!File.Exists(_statusPath)) return;

        // Deliberately NOT gated on LastWriteTime. Windows updates that lazily
        // for an open-write-close cycle, so polling on the timestamp made
        // fresh values arrive in visible steps. The file is about a kilobyte;
        // just read it and let the sequence number decide if anything changed.
        string text;
        using (var fs = new FileStream(_statusPath, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs))
            text = sr.ReadToEnd();

        if (!text.Contains("#END", StringComparison.Ordinal)) return;

        var values = new Dictionary<string, double>();
        string? aircraft = null;
        var error = "";
        int seq = _lastSeq, count = 0;
        int msTotal = 0, msRead = 0, msIo = 0;
        var clock = "";
        int ver = 0, msSleep = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '#')
            {
                if (line.StartsWith("#AC ", StringComparison.Ordinal))
                    aircraft = line[4..].Trim();
                else if (line.StartsWith("#SEQ ", StringComparison.Ordinal))
                    int.TryParse(line[5..].Trim(), out seq);
                else if (line.StartsWith("#N ", StringComparison.Ordinal))
                    int.TryParse(line[3..].Trim(), out count);
                else if (line.StartsWith("#ERR ", StringComparison.Ordinal))
                    error = line[5..].Trim();
                else if (line.StartsWith("#MS ", StringComparison.Ordinal))
                    int.TryParse(line[4..].Trim(), out msTotal);
                else if (line.StartsWith("#RD ", StringComparison.Ordinal))
                    int.TryParse(line[4..].Trim(), out msRead);
                else if (line.StartsWith("#IO ", StringComparison.Ordinal))
                    int.TryParse(line[4..].Trim(), out msIo);
                else if (line.StartsWith("#CLK ", StringComparison.Ordinal))
                    clock = line[5..].Trim();
                else if (line.StartsWith("#VER ", StringComparison.Ordinal))
                    int.TryParse(line[5..].Trim(), out ver);
                else if (line.StartsWith("#SL ", StringComparison.Ordinal))
                    int.TryParse(line[4..].Trim(), out msSleep);
                continue;
            }

            var i = line.IndexOf('=');
            if (i <= 0) continue;
            if (double.TryParse(line[(i + 1)..], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var d))
                values[line[..i]] = d;
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var changed = false;

            foreach (var kv in values)
            {
                if (!_values.TryGetValue(kv.Key, out var old)
                    || Math.Abs(old - kv.Value) > 1e-9)
                    changed = true;
                _values[kv.Key] = kv.Value;
            }

            if (aircraft is not null) _aircraft = aircraft;
            _watchCount = count;
            _scriptError = error;
            _msTotal = msTotal; _msRead = msRead; _msIo = msIo;
            if (clock.Length > 0) _clockKind = clock;
            _scriptVersion = ver;
            _msSleep = msSleep;

            if (seq != _lastSeq)
            {
                _heartbeats++;
                _lastSeq = seq;
                _beatTimes.Enqueue(now);
                Rate(_beatTimes);
            }
            if (changed) { _changeTimes.Enqueue(now); Rate(_changeTimes); }

            _lastRx = now;
        }
    }

    private void ReadScanFile()
    {
        if (!File.Exists(_scanPath)) return;

        string[] lines;
        using (var fs = new FileStream(_scanPath, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs))
            lines = sr.ReadToEnd().Split('\n');

        if (lines.Length == 0) return;
        if (!lines.Any(l => l.StartsWith("#END", StringComparison.Ordinal))) return;

        var names = lines.Select(l => l.Trim())
                         .Where(l => l.Length > 0 && l[0] != '#')
                         .ToList();
        lock (_lock)
        {
            _allLvars.Clear();
            _allLvars.AddRange(names);
        }
        ScanInProgress = false;
        ScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    // -- optional UDP fast path ---------------------------------------------

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp!.ReceiveAsync(ct);
                Ingest(Encoding.UTF8.GetString(res.Buffer));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(200, ct); }
        }
    }

    private void Ingest(string text)
    {
        lock (_lock)
        {
            foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                if (i <= 0) continue;
                var key = pair[..i].Trim();
                if (key.StartsWith('#')) continue;
                if (double.TryParse(pair[(i + 1)..].Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var d))
                    _values[key] = d;
            }
            _lastRx = DateTime.UtcNow;
        }
    }

    // -- commands -----------------------------------------------------------

    /// <summary>Queue a command. Appended, so two issued close together
    /// cannot overwrite each other.</summary>
    private void Command(string text)
    {
        try
        {
            Directory.CreateDirectory(_bridgeDir);
            File.AppendAllText(_commandPath, text + "\n");
        }
        catch (IOException) { }
    }

    /// <summary>Ask the Lua side to enumerate every LVAR in the aircraft.</summary>
    public void RequestScan()
    {
        try { if (File.Exists(_scanPath)) File.Delete(_scanPath); } catch { }
        ScanInProgress = true;
        Command("SCAN");

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 60 && ScanInProgress; i++)
            {
                await Task.Delay(250);
                try { ReadScanFile(); } catch (IOException) { }
            }
            if (ScanInProgress)
            {
                ScanInProgress = false;
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void RequestReload() => Command("RELOAD");

    // -- IDataSource --------------------------------------------------------

    public void SetWatchlist(IReadOnlyCollection<string> names)
    {
        var ordered = names.Where(n => !string.IsNullOrWhiteSpace(n))
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(n => n, StringComparer.Ordinal);

        Directory.CreateDirectory(_bridgeDir);

        // Write then move, so the Lua side never reads a half-written list.
        var tmp = _watchlistPath + ".tmp";
        File.WriteAllLines(tmp, ordered);
        File.Move(tmp, _watchlistPath, overwrite: true);
    }

    public IReadOnlyDictionary<string, double> Read()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRx >= Stale)
                return new Dictionary<string, double>();
            return new Dictionary<string, double>(_values);
        }
    }

    public bool TryWrite(string name, double value)
    {
        Command(string.Create(CultureInfo.InvariantCulture,
                              $"SET {name}={value:F6}"));
        return true;
    }
}
