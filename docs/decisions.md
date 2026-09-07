# Decision log

Why things are the way they are, in the order they were decided. Each entry
is what was tried, what was measured, and what was chosen. Read this before
undoing anything that looks odd.

---

### 1. Logical names between firmware and sim variables

Firmware subscribes to `brake.accum_psi`, never to an LVAR. A JSON profile
maps logical → raw per aircraft. Swapping Fenix for another A320 add-on is a
new profile, not a reflash.

### 2. Modules are self-describing

A panel broadcasts its id, type, firmware version and wanted values on boot.
The hub assigns slots and streams. **Adding a panel changes nothing on the
PC.** This is the property that makes "add panels without rebuilding the
software" true.

### 3. C# / .NET 8, not Python

Python was the prototype. It was replaced because everything on a Windows sim
rig speaks .NET, packaging is a single self-contained .exe, and a 30 Hz loop
should not fight the GIL. The Python tree survives as `bench-tools/` because
it runs anywhere with no toolchain and speaks the same protocol byte for byte.

### 4. Zero NuGet dependencies in the app

Tray via WinForms `NotifyIcon`, JSON via `System.Text.Json`, theming via a
`ResourceDictionary`. Nothing to restore, nothing to break in two years.
`nuget.config` keeps nuget.org only because a self-contained publish must
download the runtime packs.

### 5. Explicit 8-byte frame header

Porting to C# forced the header length to be stated as a constant, which
exposed Python packing 6 bytes while the firmware parsed 7. Fixed by making
it 8 with two reserved bytes, and by a test that asserts `HeaderLen == 8`
and another that compares C# and Python output as hex.

### 6. Tick loop on a dedicated thread with `timeBeginPeriod(1)`

Measured on real hardware: 23.7 Hz for a requested 30 Hz. `Thread.Sleep(1)`
rounds to the ~15.6 ms scheduler quantum. Fix: raise the timer resolution and
*measure* the actual sleep granularity at startup, spinning for the last
stretch. The integration test measures the achieved rate and fails outside
27–33 Hz.

### 7. Raw `TcpListener` for firmware serving, not `HttpListener`

`HttpListener` sits on HTTP.sys, which refuses to bind without a `netsh` URL
ACL - i.e. admin, every launch. On a normal account it silently never started
and every OTA download was refused. A raw socket has no such requirement. This
also removed the installer's need for elevation.

### 8. FSUIPC Lua bridge - built, measured, abandoned

Three transports were tried: UDP (`com.udpconnect` not reliably present),
`event.timer` (fired once then stopped), and a file rewritten by a plain loop.
The file version worked. Measured: loop 1766 ms, of which reading 3 variables
562 ms, writing 1 KB 672 ms, and a *1 ms sleep* 141 ms. A sleep cannot be slowed
by antivirus or WASM latency - FSUIPC schedules Lua a couple of times a second.
Hard ceiling. Kept behind `--fsuipc` only as a reference.

### 9. SimConnect direct

Since MSFS Sim Update 12 (March 2023) an LVAR can go in a data definition
like any simvar. Before that it was genuinely impossible, which is why FSUIPC
and MobiFlight ship WASM modules and why most documentation still describes
that route. Values arrive on a callback per frame with no intermediate process.

### 10. Native P/Invoke, not the managed SimConnect wrapper

`Microsoft.FlightSimulator.SimConnect.dll` is a C++/CLI mixed-mode assembly
built against .NET Framework. .NET 8 throws `BadImageFormatException` on load
- looks like a corrupt file, is not. Calling the native DLL directly leaves one
redistributable file and no build-time dependency.

### 11. Photographic face, not a drawing

Several rounds of redrawing the dial got close; the supplied clean artwork was
better than any of them and free. The face is the artwork cropped to the
circle; only the pointers are rendered. The redraw generator (`make_dial.py`)
is kept for the geometry header and as a fallback.

### 12. Pivots are fitted circle centres; scale angles are measured

Boss centres come from a least-squares circle fit to each disc's edge against
the black window (0.2–0.4 px RMS for the brakes). Centroids were biased by
face pixels and put the accumulator pivot 0.15 R too high. Scale angles are
piecewise-linear from the tick marks; the brake scales are not linear
(0→1 gets more arc than each unit of 1→3).

### 13. Face centre by rim symmetry

An edge-sharpness fit was pulled 23 px sideways by the bezel's uneven
highlight. At the true centre a ring just inside the edge is equally bright in
every direction; that is what is used, and it agrees with the lettering axis
and the boss midpoint.

### 14. Routed Gothic for any rendered lettering

SIL OFL 1.1 digitisation of the Gorton / Leroy engraving that MS33558
specifies. It is a hairline by design; glyphs are stroked as well as filled.
Only matters for the drawn fallback now that the face is photographic.

### 15. Panels are not modelled in the hub

Considered: a data-driven panel definition scheme with a generic renderer,
generic per-hardware firmware, and hub-served faces. Rejected in favour of
self-contained panel firmware sharing `SimDeckClient`. Simpler system, every
piece has one job, and nothing about a panel's appearance can ever require a
hub change. Cost: reassigning a screen is a reflash (OTA makes that trivial).

### 16. The app contains no panel-specific code

`GaugeControl` and the Panels page are gone from `SimDeck.App`. The gauge
renderer moved to `SimDeck.Bench`, where it is a bench instrument rather than
a feature implying the hub understands gauges. The app is now Devices,
Variables, Firmware, Settings, Log - and nothing about a new panel can require
changing it.

### 17. FSUIPC removed entirely

Source, Lua script, installer and docs deleted; ~600 lines and a whole class
of confusing failure gone. SimConnect is the only live route. The measurements
that justified this are in #8 and stay here as the record.

### 18. Protocol frozen at v1

The constants and a golden frame are asserted by tests. Changing one silently
breaks hardware already flashed and sitting in a cockpit. If a change is ever
needed, bump `Protocol.Version` and support both - do not edit the numbers.

### 19. Inputs are tested end to end

`ev` → profile `inputs` → sim write had never been exercised. Testing it found
that `TryWrite` only worked for variables already being *read*, so an
input-only variable failed silently. Definitions are now created on demand.

### 20. Robustness cases are tested, and tests wait on conditions

Sim restart, profile edit mid-stream, aircraft change, module power-cycle,
duplicate ids, oversized subscriptions, malformed packets. Writing them found
that a fixed `Task.Delay` is a flaky test on a slower machine; the suite waits
for conditions instead. Modelling a module that pings - as a real one does -
also exposed that the first version of the test had been asserting against an
already-timed-out module.

### 21. A pre-flight checker instead of hoping

`SimDeck.App` targets net8.0-windows and cannot be compiled on a Linux
machine, so edits to it were being verified by eye - and a whole duplicated
method block got through, which the compiler caught only on Windows.
`tools/preflight.py` now checks for duplicate members per class, malformed
XAML, properties set twice, missing `System.IO` imports and references to
deleted types.

Writing it was instructive twice over. A brace-counting check needs a
left-to-right scanner, not regex passes: stripping strings first makes the
char literal `'"'` open a phantom string, and stripping chars first makes the
apostrophe in `"simulator's"` open a phantom char literal. Both orders
produced confident, wrong reports on good files. The tool is self-tested by
injecting the real bug and confirming it is caught.
