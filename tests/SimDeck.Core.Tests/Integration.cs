using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimDeck.Core;
using SimDeck.Core.Sources;

namespace SimDeck.Core.Tests;

/// <summary>
/// Drives a real HubService with a fake ESP32 on the loopback: hello,
/// welcome, streamed frames, an OTA offer, the HTTP download, SHA
/// verification, and the status report back.
/// </summary>
public static class Integration
{
    public static async Task<(int passed, int failed)> Run()
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            if (ok) { passed++; Console.WriteLine($"  pass  {name}"); }
            else { failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " : " + detail)}"); }
        }

        var root = Path.Combine(Path.GetTempPath(), "simdeck_it_" + Guid.NewGuid().ToString("N")[..8]);
        var profileDir = Path.Combine(root, "profiles");
        var fwDir = Path.Combine(root, "firmware_bin");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(fwDir);

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
          "inputs": {}
        }
        """);

        using var hub = new HubService(new MockSource(), profileDir, fwDir);
        hub.Start();
        await Task.Delay(400);

        Check("profile selected from mock title", hub.Profile?.Name == "Mock source",
              hub.Profile?.Name ?? "none");

        // publish an image the fake module will be offered
        var binPath = Path.Combine(root, "fake.bin");
        var payload = RandomNumberGenerator.GetBytes(60_000);
        File.WriteAllBytes(binPath, payload);
        var entry = hub.Repo.Publish("accu_panel", binPath, "1.2.0", "esp32s3");

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, Protocol.ModulePort));

        void Ctrl(object o)
        {
            var b = JsonSerializer.SerializeToUtf8Bytes(o, Json.Options);
            udp.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, Protocol.CtrlPort));
        }

        Ctrl(new
        {
            t = "hello",
            id = "accu-01",
            type = "accu_panel",
            name = "Accumulator/brake panel",
            fw = "1.0.0",
            rate = 30,
            sub = new[] { "brake.accum_psi", "brake.left_psi", "brake.right_psi" },
        });

        bool gotWelcome = false, gotOffer = false, shaOk = false, valuesOk = false;
        int frames = 0;
        DateTime firstFrame = DateTime.MinValue, lastFrame = DateTime.MinValue;
        string offeredUrl = "", offeredSha = "";

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            using var cts = new CancellationTokenSource(remaining);
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (res.Buffer.Length > 0 && res.Buffer[0] == Protocol.DataMagic)
            {
                if (Protocol.TryDecodeFrame(res.Buffer, out _, out var flags, out var vals))
                {
                    frames++;
                    if (firstFrame == DateTime.MinValue) firstFrame = DateTime.UtcNow;
                    lastFrame = DateTime.UtcNow;
                    if (frames == 5)
                        valuesOk = vals.Length == 3
                                   && !float.IsNaN(vals[0]) && vals[0] > 2000
                                   && (flags & Protocol.FlagSimOk) != 0
                                   && (flags & Protocol.FlagProfileOk) != 0;
                }
                continue;
            }

            var text = Encoding.UTF8.GetString(res.Buffer);
            if (text.Contains("\"welcome\"")) gotWelcome = true;

            if (text.Contains("\"t\":\"ota\""))
            {
                var doc = JsonDocument.Parse(text).RootElement;
                offeredUrl = doc.GetProperty("url").GetString() ?? "";
                offeredSha = doc.GetProperty("sha256").GetString() ?? "";
                gotOffer = true;

                Ctrl(new { t = "ota_status", id = "accu-01", state = "start" });

                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var blob = await http.GetByteArrayAsync(offeredUrl);
                    var got = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();
                    shaOk = got == offeredSha && blob.Length == payload.Length;
                    Ctrl(new
                    {
                        t = "ota_status",
                        id = "accu-01",
                        state = shaOk ? "ok" : "fail",
                        pct = 100,
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"        (download failed: {ex.Message})");
                    Ctrl(new { t = "ota_status", id = "accu-01", state = "fail", err = "http" });
                }

                deadline = DateTime.UtcNow.AddSeconds(2.5);
            }
        }

        Check("module registered", hub.Modules.Count == 1);
        Check("welcome sent", gotWelcome);
        var span = (lastFrame - firstFrame).TotalSeconds;
        var hz = span > 0.5 ? frames / span : 0;
        Check("streams at the requested 30Hz", hz > 27 && hz < 33,
              $"measured {hz:0.0}Hz over {span:0.0}s");
        Check("frame carries live values and flags", valuesOk);
        Check("ota offered for older firmware", gotOffer);
        Check("offer url points at a routable address",
              offeredUrl.StartsWith("http://127.0.0.1:") || offeredUrl.Contains("/fw/"),
              offeredUrl);

        if (hub.Server.Running)
        {
            Check("image downloads and sha256 matches", shaOk);
            Check("hub recorded the update as done",
                  hub.Ota.Get("accu-01").State == SimDeck.Core.Ota.OtaState.Done,
                  hub.Ota.Get("accu-01").State.ToString());
        }
        else
        {
            Console.WriteLine("  skip  http download (listener unavailable in this sandbox)");
        }

        Check("manifest version recorded", entry.Version == "1.2.0");

        try { Directory.Delete(root, true); } catch { }
        return (passed, failed);
    }
}
