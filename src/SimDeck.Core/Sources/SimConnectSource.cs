using System.Runtime.InteropServices;
using static SimDeck.Core.Sources.SimConnectNative;

namespace SimDeck.Core.Sources;

/// <summary>
/// Reads LVARs straight from the simulator over SimConnect.
///
/// Since MSFS Sim Update 12 an LVAR can go into a data definition like any
/// other variable - "L:MY_VAR" instead of "AIRSPEED INDICATED" - and be
/// requested at sim-frame rate. Before SU12 that was impossible, which is why
/// FSUIPC and MobiFlight both ship a WASM module into the sim to relay LVARs
/// out, and why most documentation still describes that route.
///
/// The difference is not marginal. The FSUIPC Lua bridge measured a 1766ms
/// loop, with a requested 1ms sleep taking 141ms, because FSUIPC schedules
/// Lua threads a couple of times a second. Here the simulator pushes values
/// on change, once per frame, with no intermediate process.
///
/// One definition and one request per variable. Packing many variables into
/// one definition means unpicking a variable-length payload for no real gain.
/// </summary>
public sealed class SimConnectSource : IDataSource
{
    private const uint DefTitle = 1;
    private const uint FirstVarId = 10;

    private readonly object _lock = new();
    private readonly Dictionary<string, double> _values = new();
    private readonly Dictionary<uint, string> _idToName = new();
    private readonly Dictionary<string, uint> _nameToId = new(StringComparer.Ordinal);
    private readonly List<string> _watch = new();
    private readonly List<string> _allLvars = new();
    private readonly Queue<DateTime> _changeTimes = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    private IntPtr _handle = IntPtr.Zero;
    private CancellationTokenSource? _cts;
    private volatile bool _open;
    private volatile bool _rebuild;
    private string _lastError = "";
    private string? _aircraft;
    private uint _nextId = FirstVarId;
    private bool _dllMissing;

    public string DisplayName => "MSFS (SimConnect)";
    public bool Connected => _open;
    public string? Aircraft { get { lock (_lock) return _aircraft; } }
    public int WatchCount { get { lock (_lock) return _watch.Count; } }

    /// <summary>SimConnect cannot enumerate LVARs, so names come from
    /// elsewhere - normally the aircraft's own files.</summary>
    public IReadOnlyList<string> AllLvars
    {
        get { lock (_lock) return _allLvars.ToArray(); }
    }

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
            if (_dllMissing)
                return new("MSFS: SimConnect.dll not found",
                    "Put SimConnect.dll beside SimDeck.exe. It comes from the "
                    + "MSFS SDK (Developer Mode, Help, SDK Installer). Only the "
                    + "native DLL is needed - not the managed wrapper, which "
                    + ".NET 8 cannot load.");

            if (!_open)
                return new("MSFS: not connected",
                    _lastError.Length > 0
                        ? _lastError + " Retrying."
                        : "Waiting for the simulator. Start MSFS and load a flight.");

            return new($"MSFS: live · {ValueHz:0} changes/s · {WatchCount} var(s)",
                       WatchCount == 0
                           ? "Connected, but no variables are being requested yet."
                           : "");
        }
    }

    // -- lifecycle ----------------------------------------------------------

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var t = new Thread(() => Loop(_cts.Token))
        {
            IsBackground = true,
            Name = "SimDeck SimConnect",
        };
        t.Start();
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_handle == IntPtr.Zero && !TryOpen(ct)) continue;

            try
            {
                if (_rebuild) RebuildRequests();
                Pump();
            }
            catch (DllNotFoundException) { _dllMissing = true; return; }
            catch (Exception ex)
            {
                Teardown(ex.Message);
            }

            if (ct.WaitHandle.WaitOne(5)) break;
        }
        Teardown("");
    }

    private bool TryOpen(CancellationToken ct)
    {
        try
        {
            var hr = SimConnect_Open(out var h, "SimDeck", IntPtr.Zero, 0,
                                     IntPtr.Zero, 0);
            if (!Ok(hr) || h == IntPtr.Zero)
            {
                // Sim not running. Entirely normal; it will connect later.
                _lastError = "Simulator not running.";
                ct.WaitHandle.WaitOne(2000);
                return false;
            }

            _handle = h;
            _lastError = "";
            SetupTitle();
            _rebuild = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            _dllMissing = true;
            ct.WaitHandle.WaitOne(5000);
            return false;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            ct.WaitHandle.WaitOne(2000);
            return false;
        }
    }

    private void Teardown(string reason)
    {
        _open = false;
        _lastError = reason;
        if (_handle != IntPtr.Zero)
        {
            try { SimConnect_Close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
        lock (_lock)
        {
            _idToName.Clear();
            _nameToId.Clear();
            _nextId = FirstVarId;
        }
    }

    // -- requests -----------------------------------------------------------

    private void SetupTitle()
    {
        SimConnect_AddToDataDefinition(_handle, DefTitle, "TITLE", null,
                                       TypeString256, 0f, Unused);
        SimConnect_RequestDataOnSimObject(_handle, DefTitle, DefTitle,
                                          ObjectIdUser, PeriodSecond,
                                          FlagChanged, 0, 0, 0);
    }

    /// <summary>
    /// Add requests for anything newly watched.
    ///
    /// Definition ids are never reused. Clearing and re-adding an id while the
    /// sim still holds requests against it produces stale values that are
    /// miserable to trace, and ids cost nothing.
    /// </summary>
    private void RebuildRequests()
    {
        _rebuild = false;
        if (_handle == IntPtr.Zero || !_open) return;

        string[] names;
        lock (_lock) names = _watch.ToArray();

        foreach (var name in names)
        {
            uint id;
            lock (_lock)
            {
                if (_nameToId.ContainsKey(name)) continue;
                id = _nextId++;
                _nameToId[name] = id;
                _idToName[id] = name;
            }

            SimConnect_AddToDataDefinition(_handle, id, "L:" + name, "number",
                                           TypeFloat64, 0f, Unused);

            // SIM_FRAME with CHANGED: the sim pushes a value only when it
            // actually moves, at frame rate. Nothing here polls.
            SimConnect_RequestDataOnSimObject(_handle, id, id, ObjectIdUser,
                                              PeriodSimFrame, FlagChanged,
                                              0, 0, 0);
        }
    }

    // -- receive ------------------------------------------------------------

    private void Pump()
    {
        while (true)
        {
            var hr = SimConnect_GetNextDispatch(_handle, out var ptr, out _);
            if (!Ok(hr) || ptr == IntPtr.Zero) return;   // nothing queued
            Dispatch(ptr);
        }
    }

    private void Dispatch(IntPtr p)
    {
        var id = (uint)Marshal.ReadInt32(p, 8);          // dwID

        switch (id)
        {
            case RecvOpen:
                _open = true;
                _lastError = "";
                _rebuild = true;
                return;

            case RecvQuit:
                Teardown("The simulator closed.");
                return;

            case RecvException:
                // Nearly always a variable name that does not exist in the
                // loaded aircraft. Other requests carry on regardless.
                var code = (uint)Marshal.ReadInt32(p, 12);
                _lastError = $"SimConnect exception {code} "
                           + "(usually a variable name this aircraft does not have).";
                return;

            case RecvSimObjectData:
                ReadSimObjectData(p);
                return;
        }
    }

    private void ReadSimObjectData(IntPtr p)
    {
        var requestId = (uint)Marshal.ReadInt32(p, 12);

        if (requestId == DefTitle)
        {
            var title = Marshal.PtrToStringAnsi(p + SimObjectDataOffset);
            if (!string.IsNullOrWhiteSpace(title))
                lock (_lock) _aircraft = title.Trim();
            return;
        }

        var value = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(p, SimObjectDataOffset));

        lock (_lock)
        {
            if (!_idToName.TryGetValue(requestId, out var name)) return;
            if (!_values.TryGetValue(name, out var old)
                || Math.Abs(old - value) > 1e-9)
                _changeTimes.Enqueue(DateTime.UtcNow);
            _values[name] = value;
        }
    }

    // -- IDataSource --------------------------------------------------------

    public void SetWatchlist(IReadOnlyCollection<string> names)
    {
        lock (_lock)
        {
            _watch.Clear();
            _watch.AddRange(names);
        }
        _rebuild = true;
    }

    /// <summary>Names found elsewhere, e.g. in the aircraft's own files.</summary>
    public void SetKnownNames(IEnumerable<string> names)
    {
        lock (_lock)
        {
            _allLvars.Clear();
            _allLvars.AddRange(names);
        }
    }

    public IReadOnlyDictionary<string, double> Read()
    {
        lock (_lock) return new Dictionary<string, double>(_values);
    }

    public bool TryWrite(string name, double value)
    {
        if (_handle == IntPtr.Zero || !_open) return false;

        uint id;
        lock (_lock) if (!_nameToId.TryGetValue(name, out id)) return false;

        var buf = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buf, BitConverter.DoubleToInt64Bits(value));
            return Ok(SimConnect_SetDataOnSimObject(_handle, id, ObjectIdUser,
                                                    0, 0, 8, buf));
        }
        catch (Exception ex) { _lastError = ex.Message; return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Teardown("");
    }
}
