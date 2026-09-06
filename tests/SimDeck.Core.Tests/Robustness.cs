using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimDeck.Core;
using SimDeck.Core.Sources;

namespace SimDeck.Core.Tests;

/// <summary>
/// The things that actually happen in a cockpit: the sim restarts mid-flight,
/// a panel loses power, two panels get flashed with the same id, a profile is
/// edited while everything is running.
///
/// None of these are exotic. All of them were untested until now.
/// </summary>
public static class Robustness
{
    /// <summary>A source that can be made to drop out and come back.</summary>
    private sealed class FlakySource : IDataSource
    {
        private readonly Dictionary<string, double> _values = new()
        {
            ["RAW_A"] = 1000, ["RAW_B"] = 2000,
        };

        public bool Up { get; set; } = true;
        public string? AircraftName { get; set; } = "Test Aeroplane";

        public string DisplayName => "Flaky";
        public bool Connected => Up;
        public string? Aircraft => Up ? AircraftName : null;
        public SourceStatus Status => new(Up ? "up" : "down", "");

        public void Start() { }
        public void Dispose() { }
        public void SetWatchlist(IReadOnlyCollection<string> names) { }
        public bool TryWrite(string name, double value) => Up;

        public IReadOnlyDictionary<string, double> Read() =>
            Up ? _values : new Dictionary<string, double>();
    }

    private static string WriteProfile(string dir, string name, string match,
                                       string rawA)
    {
        var path = Path.Combine(dir, name + ".json");
        File.WriteAllText(path, $$"""
        {
          "name": "{{name}}",
          "match": ["{{match}}"],
          "vars": {
            "test.a": { "name": "{{rawA}}", "scale": 1.0 },
            "test.b": { "name": "RAW_B", "scale": 1.0 }
          },
          "inputs": {}
        }
        """);
        return path;
    }

    public static async Task<(int passed, int failed)> Run()
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            if (ok) { passed++; Console.WriteLine($"  pass  {name}"); }
            else { failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " : " + detail)}"); }
        }

        var root = Path.Combine(Path.GetTempPath(), "simdeck_rb_" + Guid.NewGuid().ToString("N")[..8]);
        var profileDir = Path.Combine(root, "profiles");
        var fwDir = Path.Combine(root, "firmware");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(fwDir);
        WriteProfile(profileDir, "test", "test aeroplane", "RAW_A");

        var source = new FlakySource();
        using var hub = new HubService(source, profileDir, fwDir);
        hub.Start();
        await Task.Delay(400);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, Protocol.ModulePort));

        // A real panel pings every 2s; without that it times out during the
        // longer waits below and every later assertion tests the wrong thing.
        var pinging = true;
        var pinger = Task.Run(async () =>
        {
            while (pinging)
            {
                Send(new { t = "ping", id = "rig-01" });
                await Task.Delay(1000);
            }
        });

        void Hello(string id) => Send(new
        {
            t = "hello", id, type = "rig", name = id, fw = "1.0.0", rate = 30,
            sub = new[] { "test.a", "test.b" },
        });

        void Send(object o)
        {
            var b = JsonSerializer.SerializeToUtf8Bytes(o, Json.Options);
            udp.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, Protocol.CtrlPort));
        }

        /// <summary>Wait for a condition rather than a fixed delay: a fixed
        /// delay that is marginal on this machine is a flaky test on a slower
        /// one, and tuning the number hides whatever made it slow.</summary>
        async Task<bool> Until(Func<bool> cond, double seconds = 3.0)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (cond()) return true;
                await Task.Delay(25);
            }
            return cond();
        }

        async Task<(int frames, float first, byte flags)> Collect(double seconds)
        {
            int n = 0; float first = float.NaN; byte flags = 0;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                using var cts = new CancellationTokenSource(left);
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                if (r.Buffer.Length > 0 && r.Buffer[0] == Protocol.DataMagic
                    && Protocol.TryDecodeFrame(r.Buffer, out _, out var f, out var v))
                {
                    n++; flags = f;
                    if (v.Length > 0) first = v[0];
                }
            }
            return (n, first, flags);
        }

        // ---- baseline ------------------------------------------------------
        Hello("rig-01");
        var b = await Collect(1.0);
        Check("streams once a module says hello", b.frames > 15, $"{b.frames} frames");
        Check("values resolve through the profile", Math.Abs(b.first - 1000) < 1,
              b.first.ToString());

        // ---- the sim goes away and comes back ------------------------------
        source.Up = false;
        await Task.Delay(300);
        var down = await Collect(0.8);
        Check("keeps streaming while the sim is down", down.frames > 10,
              $"{down.frames} frames");
        Check("clears the sim-ok flag so panels can fall back",
              (down.flags & Protocol.FlagSimOk) == 0);
        Check("sends NaN rather than a stale value", float.IsNaN(down.first),
              down.first.ToString());

        source.Up = true;
        await Task.Delay(400);
        var back = await Collect(0.8);
        Check("recovers on its own when the sim returns",
              (back.flags & Protocol.FlagSimOk) != 0 && Math.Abs(back.first - 1000) < 1,
              $"flags {back.flags}, value {back.first}");

        // ---- the aircraft changes under us ---------------------------------
        source.AircraftName = "Something Else";
        await Task.Delay(1400);            // profile is re-evaluated each second
        var noProfile = await Collect(0.6);
        Check("no matching profile clears the profile flag",
              (noProfile.flags & Protocol.FlagProfileOk) == 0, $"flags {noProfile.flags}");
        Check("and sends NaN rather than the previous aircraft's values",
              float.IsNaN(noProfile.first), noProfile.first.ToString());

        source.AircraftName = "Test Aeroplane";
        await Task.Delay(1400);
        var reProfile = await Collect(0.6);
        Check("profile re-matches when the aircraft comes back",
              (reProfile.flags & Protocol.FlagProfileOk) != 0);

        // ---- a profile is edited while running -----------------------------
        WriteProfile(profileDir, "test", "test aeroplane", "RAW_B");
        hub.ReloadProfiles();
        await Task.Delay(300);
        var edited = await Collect(0.6);
        Check("reloading a profile takes effect without a restart",
              Math.Abs(edited.first - 2000) < 1, edited.first.ToString());
        WriteProfile(profileDir, "test", "test aeroplane", "RAW_A");
        hub.ReloadProfiles();

        // ---- a panel is power-cycled ---------------------------------------
        // It stops pinging, times out, then says hello again on boot.
        var before = hub.Modules.Count;
        Check("module stays listed while it keeps pinging", before == 1, before.ToString());

        pinging = false;                    // the panel loses power
        await pinger;
        var dropped = await Until(() => hub.Modules.Count == 0, 12.0);
        Check("a silent module is dropped after the timeout", dropped,
              $"{hub.Modules.Count} still listed");

        Hello("rig-01");
        Check("and is picked straight back up when it reboots",
              await Until(() => hub.Modules.Count == 1),
              $"{hub.Modules.Count} listed");

        // ---- two panels flashed with the same id ---------------------------
        // Wrong, but it happens. It must not corrupt the registry or crash.
        Hello("rig-01");
        Hello("rig-01");
        await Task.Delay(300);
        Check("duplicate ids collapse to one entry, no crash",
              hub.Modules.Count == 1, $"{hub.Modules.Count} entries");


        // ---- a module asking for more slots than the protocol allows -------
        Send(new
        {
            t = "hello", id = "greedy-01", type = "rig", name = "greedy",
            fw = "1.0.0", rate = 30,
            sub = Enumerable.Range(0, 40).Select(i => $"test.x{i}").ToArray(),
        });
        await Until(() => hub.Modules.Any(m => m.Id == "greedy-01"));
        var greedy = hub.Modules.FirstOrDefault(m => m.Id == "greedy-01");
        Check("oversized subscription is clamped, not rejected or overflowed",
              greedy is not null && greedy.Subscriptions.Count == Protocol.MaxSlots,
              greedy is null ? "module missing" : greedy.Subscriptions.Count.ToString());

        // ---- rubbish on the control port -----------------------------------
        var junk = Encoding.UTF8.GetBytes("{ this is not json");
        udp.Send(junk, junk.Length, new IPEndPoint(IPAddress.Loopback, Protocol.CtrlPort));
        udp.Send(new byte[] { 0xFF, 0x00, 0x13 }, 3,
                 new IPEndPoint(IPAddress.Loopback, Protocol.CtrlPort));
        await Task.Delay(300);
        var after = await Collect(0.6);
        Check("malformed packets are ignored and streaming continues",
              after.frames > 8, $"{after.frames} frames");

        try { Directory.Delete(root, true); } catch { }
        return (passed, failed);
    }
}
