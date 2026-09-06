using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimDeck.Core.Ota;
using SimDeck.Core.Sources;

namespace SimDeck.Core;

public sealed class HubEventArgs : EventArgs
{
    public required string Message { get; init; }
}

/// <summary>
/// One service for every module in the cockpit.
///
/// Modules announce themselves and say what they want in logical terms
/// ("brake.accum_psi"). An aircraft profile maps those onto whatever the
/// current add-on exposes. The hub streams values back at each module's
/// requested rate, forwards module inputs into the sim, and keeps module
/// firmware up to date.
/// </summary>
public sealed class HubService : IDisposable
{
    private static readonly TimeSpan ModuleTimeout = TimeSpan.FromSeconds(8);
    private const double SourcePollHz = 60.0;

    private readonly ProfileStore _profileStore;
    private readonly object _lock = new();
    private readonly Dictionary<string, ModuleInfo> _modules = new();

    private UdpClient? _ctrl;
    private CancellationTokenSource? _cts;
    private List<AircraftProfile> _profiles = new();
    private IReadOnlyDictionary<string, double> _snapshot =
        new Dictionary<string, double>();
    private string _watchKey = "";
    private readonly HashSet<string> _probes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _virtual = new(StringComparer.Ordinal);

    public IDataSource Source { get; }
    public FirmwareRepo Repo { get; }
    public FirmwareServer Server { get; }
    public OtaTracker Ota { get; } = new();
    public AircraftProfile? Profile { get; private set; }
    public bool AutoUpdate { get; set; } = true;

    public event EventHandler<HubEventArgs>? Log;
    public event EventHandler? ModulesChanged;

    public HubService(IDataSource source, string profileDir, string firmwareDir)
    {
        Source = source;
        _profileStore = new ProfileStore(profileDir);
        Repo = new FirmwareRepo(firmwareDir);
        Server = new FirmwareServer(Repo);
    }

    public IReadOnlyList<ModuleInfo> Modules
    {
        get { lock (_lock) return _modules.Values.OrderBy(m => m.Id).ToList(); }
    }

    /// <summary>Latest raw values from the source, by raw name.</summary>
    public IReadOnlyDictionary<string, double> Snapshot => _snapshot;

    /// <summary>
    /// Raw names to poll regardless of any profile or module.
    ///
    /// This is what makes finding a variable possible: tick a name in the
    /// variables list and its live value appears, without having to guess it
    /// into a profile first and restart anything.
    /// </summary>
    public IReadOnlyCollection<string> Probes
    {
        get { lock (_lock) return _probes.ToArray(); }
    }

    public void AddProbe(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return;
        lock (_lock) _probes.Add(rawName.Trim());
        RefreshWatchlist();
    }

    public void RemoveProbe(string rawName)
    {
        lock (_lock) _probes.Remove(rawName);
        RefreshWatchlist();
    }

    /// <summary>
    /// Logical names to poll even with no hardware connected.
    ///
    /// This is what lets the on-screen gauge work before a panel exists: the
    /// watchlist is normally driven by connected modules, so without this a
    /// preview would sit at no-data forever while everything else looked fine.
    /// </summary>
    public void AddVirtualSubscription(params string[] logicalNames)
    {
        lock (_lock)
            foreach (var n in logicalNames)
                if (!string.IsNullOrWhiteSpace(n)) _virtual.Add(n.Trim());
        RefreshWatchlist(force: true);
    }

    public void ClearVirtualSubscriptions()
    {
        lock (_lock) _virtual.Clear();
        RefreshWatchlist(force: true);
    }

    /// <summary>Current value of a logical name, or NaN.</summary>
    public float ResolveLogical(string logicalName)
        => Profile is null ? float.NaN : Profile.Resolve(logicalName, _snapshot);

    public void ClearProbes()
    {
        lock (_lock) _probes.Clear();
        RefreshWatchlist();
    }

    private void Emit(string msg)
    {
        Debug.WriteLine(msg);
        Log?.Invoke(this, new HubEventArgs { Message = msg });
    }

    // -- lifecycle ----------------------------------------------------------

    public void Start()
    {
        _profiles = _profileStore.Load();
        Emit($"[deck] {_profiles.Count} profile(s) loaded");

        Source.Start();

        if (!Server.TryStart(out var err))
            Emit($"[deck] firmware server off: {err}");

        _ctrl = new UdpClient();
        _ctrl.Client.SetSocketOption(SocketOptionLevel.Socket,
                                     SocketOptionName.ReuseAddress, true);
        _ctrl.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.CtrlPort));
        _ctrl.EnableBroadcast = true;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoop(_cts.Token));

        // A dedicated thread, not the pool: this loop must not queue behind
        // whatever else the app is doing, and it briefly spins.
        var tick = new Thread(() => TickLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "SimDeck tick",
            Priority = ThreadPriority.AboveNormal,
        };
        tick.Start();

        Emit($"[deck] control udp/{Protocol.CtrlPort}");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        RestoreTimerResolution();
        _ctrl?.Dispose();
        Server.Dispose();
        Source.Dispose();
    }

    // -- control plane ------------------------------------------------------

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await _ctrl!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            try { Handle(Encoding.UTF8.GetString(res.Buffer), res.RemoteEndPoint); }
            catch (Exception ex) { Emit($"[deck] bad message: {ex.Message}"); }
        }
    }

    private void Handle(string text, IPEndPoint from)
    {
        var env = JsonSerializer.Deserialize<EnvelopeMessage>(text, Json.Options);
        if (env?.Id is null) return;

        switch (env.Type)
        {
            case "hello":
                var hello = JsonSerializer.Deserialize<HelloMessage>(text, Json.Options);
                if (hello is not null) OnHello(hello, from);
                break;

            case "ping":
                lock (_lock)
                {
                    if (_modules.TryGetValue(env.Id, out var m))
                    {
                        m.LastSeen = DateTime.UtcNow;
                        SendJson(m.Ip, new { t = "pong" });
                    }
                }
                break;

            case "ev":       OnInput(env); break;
            case "ota_status": OnOtaStatus(env); break;
        }
    }

    private void OnHello(HelloMessage hello, IPEndPoint from)
    {
        var subs = hello.Subscriptions.Take(Protocol.MaxSlots).ToList();
        bool fresh;
        ModuleInfo mod;

        lock (_lock)
        {
            _modules.TryGetValue(hello.Id, out var known);
            fresh = known is null
                    || !known.Subscriptions.SequenceEqual(subs)
                    || known.Ip != from.Address.ToString()
                    || known.Firmware != (hello.Firmware ?? "0.0.0");

            mod = known ?? new ModuleInfo
            {
                Id = hello.Id,
                Name = hello.Name ?? hello.Id,
                ModuleType = hello.ModuleType ?? hello.Id.Split('-')[0],
                Firmware = hello.Firmware ?? "0.0.0",
                Ip = from.Address.ToString(),
                Subscriptions = subs,
            };

            mod.Name = hello.Name ?? mod.Name;
            mod.ModuleType = hello.ModuleType ?? mod.ModuleType;
            mod.Firmware = hello.Firmware ?? mod.Firmware;
            mod.Ip = from.Address.ToString();
            mod.Subscriptions = subs;
            mod.Rate = Math.Clamp(hello.Rate, 1, 60);
            mod.LastSeen = DateTime.UtcNow;
            if (mod.Values.Length != subs.Count) mod.Values = new float[subs.Count];

            _modules[hello.Id] = mod;
        }

        if (fresh)
        {
            Emit($"[deck] + {mod.Name} ({mod.Id}) @ {mod.Ip} fw {mod.Firmware} " +
                 $"{subs.Count} value(s) @ {mod.Rate:0}Hz");
            RefreshWatchlist();
            ModulesChanged?.Invoke(this, EventArgs.Empty);
        }

        var slots = new Dictionary<string, int>();
        for (int i = 0; i < subs.Count; i++) slots[subs[i]] = i;

        SendJson(mod.Ip, new
        {
            t = "welcome",
            hub = 1,
            slots,
            rate = mod.Rate,
            unknown = Profile is null
                ? Array.Empty<string>()
                : subs.Where(s => !Profile.Vars.ContainsKey(s)).ToArray(),
        });

        if (AutoUpdate) MaybeOfferOta(mod);
    }

    private void OnInput(EnvelopeMessage env)
    {
        ModuleInfo? mod;
        lock (_lock) _modules.TryGetValue(env.Id!, out mod);
        if (mod is null) return;
        mod.LastSeen = DateTime.UtcNow;

        if (Profile is null || env.Input is null) return;
        if (!Profile.Inputs.TryGetValue(env.Input, out var spec))
        {
            Emit($"[deck] {env.Id}: no mapping for input '{env.Input}'");
            return;
        }
        if (!Source.TryWrite(spec.Name, env.Value * spec.Scale))
            Emit($"[deck] source rejected write {spec.Name}");
    }

    private void OnOtaStatus(EnvelopeMessage env)
    {
        var id = env.Id!;
        switch (env.State)
        {
            case "start":
                Ota.Set(id, OtaState.Running, 0, message: "downloading"); break;
            case "progress":
                Ota.Set(id, OtaState.Running, env.Percent, message: "downloading"); break;
            case "ok":
                Ota.Set(id, OtaState.Done, 100, message: "rebooting");
                Emit($"[deck] {id}: firmware updated"); break;
            case "fail":
                Ota.Set(id, OtaState.Failed, message: env.Error ?? "failed");
                Emit($"[deck] {id}: OTA failed: {env.Error}"); break;
        }
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SendJson(string ip, object payload)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
            _ctrl!.Send(bytes, bytes.Length,
                        new IPEndPoint(IPAddress.Parse(ip), Protocol.ModulePort));
        }
        catch (Exception ex) { Emit($"[deck] send failed to {ip}: {ex.Message}"); }
    }

    // -- OTA ----------------------------------------------------------------

    /// <summary>
    /// Which of our addresses can that module actually reach us on?
    ///
    /// Matters on a PC with a VPN or several NICs, where the first address
    /// the OS reports is often not the one on the cockpit LAN. Get this wrong
    /// and the module downloads from an address that does not route, and OTA
    /// fails with nothing useful in the log.
    /// </summary>
    public static string LocalAddressFor(string peer)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork,
                                     SocketType.Dgram, ProtocolType.Udp);
            s.Connect(peer, 9);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    private void Offer(ModuleInfo mod, FirmwareEntry entry)
    {
        var host = LocalAddressFor(mod.Ip);
        var url = $"http://{host}:{Server.Port}/fw/{entry.File}";

        SendJson(mod.Ip, new
        {
            t = "ota",
            url,
            ver = entry.Version,
            sha256 = entry.Sha256,
            size = entry.Size,
        });

        Ota.Set(mod.Id, OtaState.Offered, 0, entry.Version, "offered");
        Emit($"[deck] {mod.Id}: offering fw {entry.Version} ({entry.File})");
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MaybeOfferOta(ModuleInfo mod)
    {
        var entry = Repo.Get(mod.ModuleType);
        if (entry is null) return;
        if (!OtaTracker.IsNewer(entry.Version, mod.Firmware)) return;
        if (!Ota.ShouldOffer(mod.Id)) return;
        Offer(mod, entry);
    }

    /// <summary>Push regardless of version. Clears any backoff first.</summary>
    public (bool ok, string error) ForceOta(string moduleId)
    {
        ModuleInfo? mod;
        lock (_lock) _modules.TryGetValue(moduleId, out mod);
        if (mod is null) return (false, "module not connected");

        var entry = Repo.Get(mod.ModuleType);
        if (entry is null)
            return (false, $"no firmware published for type '{mod.ModuleType}'");

        Ota.Set(moduleId, OtaState.Idle);
        Offer(mod, entry);
        return (true, "");
    }

    /// <summary>Update every connected module of a type that is behind.</summary>
    public int UpdateAllOfType(string moduleType)
    {
        int n = 0;
        var entry = Repo.Get(moduleType);
        if (entry is null) return 0;

        foreach (var mod in Modules.Where(m => m.ModuleType == moduleType))
        {
            if (!OtaTracker.IsNewer(entry.Version, mod.Firmware)) continue;
            Ota.Set(mod.Id, OtaState.Idle);
            Offer(mod, entry);
            n++;
        }
        return n;
    }

    public void Identify(string moduleId)
    {
        ModuleInfo? mod;
        lock (_lock) _modules.TryGetValue(moduleId, out mod);
        if (mod is not null) SendJson(mod.Ip, new { t = "identify" });
    }

    // -- profile / watchlist ------------------------------------------------

    private void PickProfile()
    {
        var chosen = _profileStore.Best(_profiles, Source.Aircraft);
        if (ReferenceEquals(chosen, Profile)) return;

        Profile = chosen;
        Emit($"[deck] aircraft='{Source.Aircraft}' -> profile={chosen?.Name ?? "none"}");
        RefreshWatchlist(force: true);
    }

    public void ReloadProfiles()
    {
        _profiles = _profileStore.Load();
        Profile = null;
        PickProfile();
    }

    private void RefreshWatchlist(bool force = false)
    {
        var logical = new HashSet<string>();
        lock (_lock)
        {
            foreach (var m in _modules.Values)
                foreach (var s in m.Subscriptions) logical.Add(s);
            foreach (var v in _virtual) logical.Add(v);
        }

        var fromProfile = Profile is null
            ? Enumerable.Empty<string>()
            : Profile.RawNames(logical);

        string[] probes;
        lock (_lock) probes = _probes.ToArray();

        var raw = fromProfile.Concat(probes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var key = string.Join('\u0001', raw);
        if (!force && key == _watchKey) return;

        _watchKey = key;
        Source.SetWatchlist(raw);
        Emit($"[deck] watching {raw.Count} raw name(s)");
    }

    // -- data plane ---------------------------------------------------------

    private byte Flags()
    {
        byte f = 0;
        if (Source.Connected) f |= Protocol.FlagSimOk;
        if (Profile is not null) f |= Protocol.FlagProfileOk;
        return f;
    }

    private void TickLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        RaiseTimerResolution();
        CalibrateSleep();

        // Resolve the profile before the first frame goes out. Waiting for
        // the one-second housekeeping tick meant the first ~30 frames left
        // as NaN, which a panel shows as a dead needle at boot.
        try { PickProfile(); } catch (Exception ex) { Emit(ex.Message); }

        long lastPoll = 0, lastProfile = sw.ElapsedTicks;
        var pollTicks = (long)(Stopwatch.Frequency / SourcePollHz);

        while (!ct.IsCancellationRequested)
        {
            var now = sw.ElapsedTicks;

            if (now - lastProfile > Stopwatch.Frequency)
            {
                lastProfile = now;
                try { PickProfile(); } catch (Exception ex) { Emit(ex.Message); }
            }

            if (now - lastPoll >= pollTicks)
            {
                lastPoll = now;
                try { _snapshot = Source.Read(); }
                catch (Exception ex) { Emit($"[deck] source read: {ex.Message}"); }
            }

            Stream(now, sw);
            Reap();

            if (!WaitPrecise(sw, ct)) return;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    private bool _timerRaised;
    private long _spinThresholdTicks;

    /// <summary>
    /// Ask Windows for 1ms timer resolution.
    ///
    /// Thread.Sleep(1) otherwise sleeps for a full scheduler quantum, about
    /// 15.6ms. With a 33ms send period that overshoots to roughly 46ms and
    /// a requested 30Hz stream actually runs at 22Hz. Measured on real
    /// hardware: 23.7Hz before this call.
    /// </summary>
    private void RaiseTimerResolution()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _timerRaised = TimeBeginPeriod(1) == 0; } catch { _timerRaised = false; }
    }

    private void RestoreTimerResolution()
    {
        if (!_timerRaised) return;
        try { TimeEndPeriod(1); } catch { }
        _timerRaised = false;
    }

    /// <summary>
    /// Measure how long Thread.Sleep(1) really takes, and spin for at least
    /// that long before each deadline.
    ///
    /// Belt and braces: if timeBeginPeriod is unavailable, or a future
    /// Windows ignores it, the loop still hits its rate by spinning more.
    /// It costs CPU rather than accuracy, which is the right way round.
    /// </summary>
    private void CalibrateSleep()
    {
        var sw = Stopwatch.StartNew();
        long worst = 0;
        for (int i = 0; i < 5; i++)
        {
            var t0 = sw.ElapsedTicks;
            Thread.Sleep(1);
            worst = Math.Max(worst, sw.ElapsedTicks - t0);
        }

        // one full observed sleep, plus 25% margin, floor of 2ms
        _spinThresholdTicks = Math.Max(Stopwatch.Frequency / 500, worst * 5 / 4);
        Emit($"[deck] sleep granularity {worst * 1000.0 / Stopwatch.Frequency:0.0}ms, " +
             $"spinning the last {_spinThresholdTicks * 1000.0 / Stopwatch.Frequency:0.0}ms");
    }

    /// <summary>Sleep until roughly the next send is due, then spin in.</summary>
    private bool WaitPrecise(Stopwatch sw, CancellationToken ct)
    {
        long next;
        lock (_lock)
        {
            next = _modules.Count == 0
                ? sw.ElapsedTicks + Stopwatch.Frequency / 100
                : _modules.Values.Min(m => m.NextTxTicks);
        }

        while (!ct.IsCancellationRequested)
        {
            var remain = next - sw.ElapsedTicks;
            if (remain <= 0) return true;
            if (remain > _spinThresholdTicks) Thread.Sleep(1);
            else Thread.SpinWait(40);
        }
        return false;
    }

    private void Stream(long now, Stopwatch sw)
    {
        var flags = Flags();
        List<ModuleInfo> due;
        lock (_lock) due = _modules.Values.Where(m => now >= m.NextTxTicks).ToList();

        foreach (var mod in due)
        {
            var vals = new float[mod.Subscriptions.Count];
            for (int i = 0; i < vals.Length; i++)
                vals[i] = Profile is null
                    ? float.NaN
                    : Profile.Resolve(mod.Subscriptions[i], _snapshot);

            mod.Values = vals;

            try
            {
                var frame = Protocol.EncodeFrame(mod.Sequence, flags, vals);
                _ctrl!.Send(frame, frame.Length,
                            new IPEndPoint(IPAddress.Parse(mod.Ip), Protocol.ModulePort));
            }
            catch (SocketException) { /* module went away; Reap will handle it */ }

            mod.Sequence++;
            // Schedule from now rather than accumulating, so a stalled tick
            // does not produce a burst of catch-up frames.
            mod.NextTxTicks = now + (long)(Stopwatch.Frequency / mod.Rate);
        }
    }

    private void Reap()
    {
        List<ModuleInfo> dead;
        lock (_lock)
        {
            dead = _modules.Values
                .Where(m => DateTime.UtcNow - m.LastSeen > ModuleTimeout).ToList();
            foreach (var m in dead) _modules.Remove(m.Id);
        }
        if (dead.Count == 0) return;

        // A module mid-OTA goes quiet while it flashes and reboots. Dropping
        // it from the live list is right; wiping its OTA record is not, so the
        // tracker is deliberately left alone here.
        foreach (var m in dead) Emit($"[deck] - {m.Name} (timeout)");
        RefreshWatchlist();
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }
}
