using System.Text;
using SimDeck.Core;
using SimDeck.Core.Ota;
using SimDeck.Core.Sources;

// Deliberately a plain console runner rather than xunit: it needs no NuGet
// packages, so the tests run anywhere the SDK is installed.

int failed = 0, passed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  pass  {name}"); }
    else { failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " : " + detail)}"); }
}

Console.WriteLine("protocol");

// The bug this suite exists for: the Python header packed to 6 bytes while
// the firmware parsed 7, so every frame was misread on real hardware.
Check("header is exactly 8 bytes", Protocol.HeaderLen == 8,
      $"got {Protocol.HeaderLen}");

var frame = Protocol.EncodeFrame(1234, Protocol.FlagSimOk,
                                 new[] { 2947f, 2698f, 2713f });
Check("frame length = header + 4*n", frame.Length == 8 + 12,
      $"got {frame.Length}");
Check("payload is 4-byte aligned", Protocol.HeaderLen % 4 == 0);
Check("magic byte", frame[0] == 0x5A);

var ok = Protocol.TryDecodeFrame(frame, out var seq, out var flags, out var vals);
Check("round trips", ok && seq == 1234 && flags == Protocol.FlagSimOk
                        && vals.Length == 3 && Math.Abs(vals[1] - 2698f) < 0.001f);

Check("rejects truncated", !Protocol.TryDecodeFrame(frame.AsSpan(0, 12), out _, out _, out _));
Check("rejects bad magic", !Protocol.TryDecodeFrame(new byte[] { 1, 1, 0, 0, 0, 0, 0, 0 },
                                                     out _, out _, out _));

var nanFrame = Protocol.EncodeFrame(0, 0, new[] { float.NaN });
Protocol.TryDecodeFrame(nanFrame, out _, out _, out var nanVals);
Check("NaN survives the wire", float.IsNaN(nanVals[0]));

Console.WriteLine("versions");
Check("1.10.0 > 1.9.0", OtaTracker.IsNewer("1.10.0", "1.9.0"));
Check("1.0.0 not newer than 1.0.0", !OtaTracker.IsNewer("1.0.0", "1.0.0"));
Check("date stamps compare", OtaTracker.IsNewer("2026.09.05", "2026.08.31"));
Check("empty current treated as zero", OtaTracker.IsNewer("0.0.1", ""));

Console.WriteLine("profile matching");
var specific = new AircraftProfile
{
    Name = "Fenix",
    Match = new() { "fenix a320", "fnx" },
    Vars = new() { ["brake.accum_psi"] = new VarSpec { Name = "FNX_ACC", Scale = 1 } }
};
var generic = new AircraftProfile { Name = "Generic A320", Match = new() { "a320" } };
var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "simdeck_test_profiles"));

var best = store.Best(new[] { generic, specific }, "Fenix A320 IAE");
Check("longest match wins over load order", best?.Name == "Fenix", best?.Name);
Check("no match returns null", store.Best(new[] { specific }, "Cessna 172") is null);
Check("null aircraft returns null", store.Best(new[] { specific }, null) is null);

// This is what bit the mock: a bench source whose title contained a real
// aircraft name silently matched the wrong profile.
Check("mock title matches no aircraft profile",
      store.Best(new[] { generic, specific }, new MockSource().Aircraft) is null);

Console.WriteLine("value resolution");
var snap = new Dictionary<string, double> { ["FNX_ACC"] = 2.5 };
specific.Vars["brake.accum_psi"].Scale = 1000;
Check("scale applied", Math.Abs(specific.Resolve("brake.accum_psi", snap) - 2500f) < 0.01f);

specific.Vars["brake.accum_psi"].Clamp = new double?[] { 0, 2000 };
Check("clamp applied", Math.Abs(specific.Resolve("brake.accum_psi", snap) - 2000f) < 0.01f);
Check("unknown logical name is NaN", float.IsNaN(specific.Resolve("nope", snap)));
Check("missing raw value is NaN",
      float.IsNaN(specific.Resolve("brake.accum_psi", new Dictionary<string, double>())));

Console.WriteLine("firmware repo");
var dir = Path.Combine(Path.GetTempPath(), "simdeck_test_fw_" + Guid.NewGuid().ToString("N")[..8]);
var repo = new FirmwareRepo(dir);
var binPath = Path.Combine(Path.GetTempPath(), "fake.bin");
File.WriteAllBytes(binPath, Encoding.UTF8.GetBytes(new string('x', 4096)));

var entry = repo.Publish("accu_panel", binPath, "1.2.0", "esp32s3");
Check("publish records size", entry.Size == 4096);
Check("publish records sha256", entry.Sha256.Length == 64);
Check("get returns entry", repo.Get("accu_panel")?.Version == "1.2.0");
Check("unknown type returns null", repo.Get("nope") is null);

File.Delete(Path.Combine(dir, entry.File));
Check("missing image file reports as absent", repo.Get("accu_panel") is null);

Directory.Delete(dir, true);

Console.WriteLine("firmware image names");
{
    var clock = new DateTime(2026, 9, 5);
    bool P(string f, out string t, out string v)
        => FirmwareImageName.TryParse(f, clock, out t, out v, out _);

    // what the Arduino IDE actually writes
    Check("plain .ino.bin accepted", P("accu_panel.ino.bin", out var t1, out var v1)
          && t1 == "accu_panel" && v1 == "2026.09.05", $"{t1} / {v1}");
    Check("board suffix stripped", P("accu_panel.ino.esp32s3.bin", out var t2, out _)
          && t2 == "accu_panel", t2);

    // merged and bootloader images are not app partitions
    foreach (var bad in new[] { "accu_panel.ino.merged.bin",
                                "accu_panel.ino.bootloader.bin",
                                "accu_panel.ino.partitions.bin" })
    {
        FirmwareImageName.TryParse(bad, clock, out _, out _, out var why);
        Check($"rejects {bad.Split('.')[^2]}",
              !P(bad, out _, out _) && why.Contains("over-the-air"), why);
    }

    // explicit versions still win
    Check("underscore version", P("accu_panel_1.2.0.bin", out var t3, out var v3)
          && t3 == "accu_panel" && v3 == "1.2.0", $"{t3} / {v3}");
    Check("hyphen version", P("fcu-panel-2.0.bin", out var t4, out var v4)
          && t4 == "fcu-panel" && v4 == "2.0", $"{t4} / {v4}");
    Check("underscored type keeps its name",
          P("accu_brake_panel_1.0.0.bin", out var t5, out var v5)
          && t5 == "accu_brake_panel" && v5 == "1.0.0", $"{t5} / {v5}");

    Check("non-bin rejected", !P("accu_panel.hex", out _, out _));

    // a date stamp must sort above an older date and below a newer one
    Check("date stamp orders correctly",
          OtaTracker.IsNewer("2026.09.05", "2026.08.31")
          && !OtaTracker.IsNewer("2026.09.05", "2026.09.06"));
}

Console.WriteLine("protocol v1 is frozen");
{
    // These values are the contract between this app, the Python bench tools
    // and every panel's firmware. Changing one silently breaks hardware that
    // is already flashed and in a cockpit. If a change is genuinely needed,
    // bump Protocol.Version and support both - do not edit these numbers.
    Check("version == 1", Protocol.Version == 1);
    Check("header == 8 bytes", Protocol.HeaderLen == 8);
    Check("magic == 0x5A", Protocol.DataMagic == 0x5A);
    Check("control port == 27500", Protocol.CtrlPort == 27500);
    Check("module port == 27501", Protocol.ModulePort == 27501);
    Check("http port == 27502", Protocol.HttpPort == 27502);
    Check("max slots == 16", Protocol.MaxSlots == 16);
    Check("flag sim ok == 0x01", Protocol.FlagSimOk == 0x01);
    Check("flag profile ok == 0x02", Protocol.FlagProfileOk == 0x02);

    // A known frame, byte for byte. Any layout change breaks this.
    var golden = Convert.ToHexString(
        Protocol.EncodeFrame(1234, Protocol.FlagSimOk,
                             new[] { 2947f, 2698f, 2713f })).ToLowerInvariant();
    Check("golden frame unchanged",
          golden == "5a01d204030100000030384500a0284500902945", golden);
}

Console.WriteLine("lvar catalog");
{
    // shapes that actually appear in MSFS behaviour files
    var xml = """
    <Component ID="BRAKE">
      <UseTemplate Name="ASOBO_GT_Push_Button">
        <VAR_NAME>L:FNX320_BRAKE_ACCU_PRESS</VAR_NAME>
        <SIMVAR_NAME VAR_NAME="L:FNX320_BRAKE_LEFT_PRESS"/>
      </UseTemplate>
      <Parameters>
        (L:FNX320_BRAKE_RIGHT_PRESS, number) 1000 *
        (L:FNX320_PARK_BRAKE_LEVER) (A:BRAKE PARKING POSITION, bool)
      </Parameters>
    </Component>
    """;

    var names = LvarCatalog.FromXml(xml);
    Check("element form found", names.Contains("FNX320_BRAKE_ACCU_PRESS"));
    Check("attribute form found", names.Contains("FNX320_BRAKE_LEFT_PRESS"));
    Check("inline RPN reference found", names.Contains("FNX320_BRAKE_RIGHT_PRESS"));
    Check("L: prefix stripped", !names.Any(n => n.StartsWith("L:")));
    Check("A: simvars not collected", !names.Any(n => n.Contains("PARKING")));
    Check("names are unique", names.Count == names.Distinct().Count());

    var ranked = LvarCatalog.Rank(names, "BRAKE", "ACCU", "PRESS");
    Check("most specific name ranks first",
          ranked[0] == "FNX320_BRAKE_ACCU_PRESS", ranked.Count > 0 ? ranked[0] : "none");
    Check("unrelated names excluded",
          !LvarCatalog.Rank(new[] { "FNX320_FUEL_QTY" }, "BRAKE").Any());
    Check("ranking is case insensitive",
          LvarCatalog.Rank(new[] { "fnx320_brake_x" }, "BRAKE").Count == 1);
}

Console.WriteLine();
Console.WriteLine("integration (real hub, fake module on loopback)");
var (ip, ifail) = await SimDeck.Core.Tests.Integration.Run();
passed += ip; failed += ifail;

Console.WriteLine();
Console.WriteLine("robustness (sim drops, module reboots, bad input)");
var (rp, rf) = await SimDeck.Core.Tests.Robustness.Run();
passed += rp; failed += rf;

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
