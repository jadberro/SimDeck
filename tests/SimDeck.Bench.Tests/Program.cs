using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using SimDeck.Bench.Network;
using SimDeck.Core;
using SimDeck.Core.Sources;

namespace SimDeck.Bench.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  pass  {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " : " + detail)}");
        }
    }

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("SimDeck.Bench End-to-End Integration Tests (Loopback UDP / HTTP)");

        var root = Path.Combine(Path.GetTempPath(), "simdeck_bench_test_" + Guid.NewGuid().ToString("N")[..8]);
        var profileDir = Path.Combine(root, "profiles");
        var fwDir = Path.Combine(root, "firmware_bin");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(fwDir);

        try
        {
            // 1. Write mock aircraft profile
            File.WriteAllText(Path.Combine(profileDir, "mock.json"), """
            {
              "name": "Mock source",
              "match": ["simdeck mock bench"],
              "priority": 100,
              "vars": {
                "brake.accum_psi": { "name": "MOCK_ACCUM_PSI", "scale": 1.0 },
                "brake.left_psi":  { "name": "MOCK_BRAKE_L_PSI", "scale": 1.0 },
                "brake.right_psi": { "name": "MOCK_BRAKE_R_PSI", "scale": 1.0 }
              },
              "inputs": {
                "park_brake": { "name": "MOCK_PARK_BRAKE", "scale": 1.0 },
                "brake_left": { "name": "MOCK_BRAKE_LEFT", "scale": 1.0 }
              }
            }
            """);

            var mock = new MockSource();
            using var hub = new HubService(mock, profileDir, fwDir);
            hub.Start();

            // Give hub 200ms to spin up
            await Task.Delay(200);

            // 2. Start Bench Client
            using var bench = new BenchPanelClient();

            // Test 1: Handshake & Link
            var linkDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < linkDeadline && !bench.IsLinked)
                await Task.Delay(50);

            Check("Bench links to Hub (welcome + slot mapping)", bench.IsLinked);

            // Test 2: Streamed Binary Data Frames at ~30Hz
            int frameCount = 0;
            double lastAccum = 0, lastLeft = 0, lastRight = 0;
            bool hadLive = false;

            bench.ValuesReceived += vals =>
            {
                frameCount++;
                lastAccum = vals.accum;
                lastLeft = vals.left;
                lastRight = vals.right;
                if (vals.live) hadLive = true;
            };

            var streamSw = Stopwatch.StartNew();
            while (streamSw.ElapsedMilliseconds < 1200 && frameCount < 25)
                await Task.Delay(40);

            Check("Bench receives binary stream at ~30Hz", frameCount >= 20, $"got {frameCount} frames in {streamSw.ElapsedMilliseconds}ms");
            Check("Stream carries valid live flags", hadLive && bench.Live);
            Check("Pressure values populated from mock", lastAccum > 100 || lastLeft > 0 || lastRight > 0,
                  $"accum={lastAccum}, left={lastLeft}, right={lastRight}");

            // Test 3: Interactive Inputs (Bench -> Hub -> Sim Source)
            bench.SendInput("park_brake", 1.0);
            var inputDeadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < inputDeadline && !mock.Writes.Any(w => w.name == "MOCK_PARK_BRAKE" && Math.Abs(w.value - 1.0) < 0.001))
                await Task.Delay(50);

            var wroteParkBrake = mock.Writes.Any(w => w.name == "MOCK_PARK_BRAKE" && Math.Abs(w.value - 1.0) < 0.001);
            Check("Interactive input routes to simulator source", wroteParkBrake,
                  $"writes: {string.Join(", ", mock.Writes.Select(w => $"{w.name}={w.value}"))}");

            // Test 4: OTA Download & SHA-256 Verification
            var binPath = Path.Combine(root, "accu_panel_1.2.0.bin");
            var fwPayload = RandomNumberGenerator.GetBytes(32768);
            File.WriteAllBytes(binPath, fwPayload);

            bool otaDone = false;
            string? otaErr = null;
            bench.OtaProgress += (state, pct, err) =>
            {
                if (state == "ok") otaDone = true;
                if (state == "fail") otaErr = err;
            };

            hub.Repo.Publish("accu_panel", binPath, "1.2.0", "esp32s3");
            hub.UpdateAllOfType("accu_panel");

            var otaDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < otaDeadline && !otaDone && otaErr is null)
                await Task.Delay(100);

            Check("OTA download and SHA-256 verified end-to-end", otaDone && bench.CurrentFw == "1.2.0",
                  $"done={otaDone}, fw={bench.CurrentFw}, err={otaErr}");

            // Test 5: Sim / Hub Shutdown triggers timeout and falls to zero
            hub.Dispose();

            var timeoutDeadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < timeoutDeadline && bench.Live)
                await Task.Delay(100);

            Check("Hub silence drops Live flag (needles fall to zero)", !bench.Live);
        }
        catch (Exception ex)
        {
            Check("Unexpected exception", false, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
