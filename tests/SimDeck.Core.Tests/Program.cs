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

Console.WriteLine("fsuipc file bridge");
{
    var bridgeDir = Path.Combine(Path.GetTempPath(), "simdeck_bridge_" + Guid.NewGuid().ToString("N")[..8]);
    var src = new FsuipcLuaSource(bridgeDir);
    src.Start();
    var status = Path.Combine(bridgeDir, "status.txt");

    // A file without #END was caught mid-write and must be ignored, not
    // parsed as a set of variables that suddenly vanished.
    File.WriteAllText(status, "#SEQ 1\n#AC Fenix A320\n#N 2\nFNX_A=1.0\n");
    Thread.Sleep(200);
    Check("incomplete file ignored", !src.Connected && src.Heartbeats == 0);

    File.WriteAllText(status,
        "#SEQ 2\n#AC Fenix A320 IAE\n#N 2\nFNX_A=12.5\nFNX_B=-3.25\n#END\n");
    Thread.Sleep(250);

    Check("complete file marks connected", src.Connected);
    Check("aircraft read", src.Aircraft == "Fenix A320 IAE", src.Aircraft);
    Check("watch count read", src.WatchCount == 2, src.WatchCount.ToString());
    var bs = src.Read();
    Check("value read", bs.ContainsKey("FNX_A") && Math.Abs(bs["FNX_A"] - 12.5) < 0.001);
    Check("negative value read", Math.Abs(bs["FNX_B"] + 3.25) < 0.001);
    Check("comment lines are not variables", !bs.Keys.Any(k => k.StartsWith("#")));

    var beats = src.Heartbeats;
    File.WriteAllText(status,
        "#SEQ 3\n#AC Fenix A320 IAE\n#N 2\nFNX_A=99\nFNX_B=-3.25\n#END\n");
    Thread.Sleep(250);
    Check("new sequence counts as a beat", src.Heartbeats > beats);

    // writes and commands are queued to a file, so two in quick succession
    // cannot overwrite one another
    src.TryWrite("FNX_TEST", 1);
    src.TryWrite("FNX_OTHER", 0);
    var cmds = File.ReadAllLines(Path.Combine(bridgeDir, "command.txt"));
    Check("commands are appended, not overwritten", cmds.Length == 2, $"{cmds.Length}");
    Check("command format", cmds[0].StartsWith("SET FNX_TEST="), cmds[0]);

    // an error reported by the script must surface, not hide in a log
    File.WriteAllText(status,
        "#SEQ 4\n#AC Fenix A320 IAE\n#N 2\n#ERR attempt to index a nil value\nFNX_A=1\n#END\n");
    Thread.Sleep(250);
    Check("script error surfaced",
          src.ScriptError.Contains("nil value"), src.ScriptError);

    File.WriteAllText(status, "#SEQ 5\n#AC Fenix A320 IAE\n#N 2\nFNX_A=1\n#END\n");
    Thread.Sleep(250);
    Check("script error cleared on recovery", src.ScriptError.Length == 0);

    // timing fields must be picked up, and must not be mistaken for variables
    File.WriteAllText(status,
        "#SEQ 6\n#AC Fenix A320 IAE\n#N 2\n#MS 940\n#RD 900\n#IO 12\nFNX_A=1\n#END\n");
    Thread.Sleep(250);
    Check("loop timing read", src.LoopMs == 940, src.LoopMs.ToString());
    Check("read timing read", src.ReadMs == 900, src.ReadMs.ToString());
    Check("write timing read", src.WriteMs == 12, src.WriteMs.ToString());
    Check("timing markers are not variables",
          !src.Read().Keys.Any(k => k.StartsWith("#")));

    // The two rates must be measured separately: a bridge writing quickly
    // while values sit still means the ceiling is upstream in FSUIPC.
    for (int i = 10; i < 20; i++)
    {
        // same value every time - writes happen, nothing changes
        File.WriteAllText(status,
            $"#SEQ {i}\n#AC Fenix A320 IAE\n#N 1\nFNX_A=42\n#END\n");
        Thread.Sleep(60);
    }
    var writesOnly = src.BeatHz;
    var changesOnly = src.ValueHz;
    Check("writes counted", writesOnly > 3, $"{writesOnly:0.0}/s");
    Check("unchanged values do not count as changes",
          changesOnly < writesOnly, $"beats {writesOnly:0.0} vs values {changesOnly:0.0}");

    for (int i = 20; i < 30; i++)
    {
        File.WriteAllText(status,
            $"#SEQ {i}\n#AC Fenix A320 IAE\n#N 1\nFNX_A={i}\n#END\n");
        Thread.Sleep(60);
    }
    Check("changing values are counted", src.ValueHz > 2, $"{src.ValueHz:0.0}/s");

    src.Dispose();
    try { Directory.Delete(bridgeDir, true); } catch { }
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
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
