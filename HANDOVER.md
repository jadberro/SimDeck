# HANDOVER

Everything an agent or developer needs to pick this project up cold. Written
6 September 2026, at the end of Phase 1.

Read this file, then `docs/decisions.md`. Those two are the whole context.

---

## 1. What this is

SimDeck drives physical instrument panels in a home flight-simulator cockpit
from Microsoft Flight Simulator 2024. One Windows application reads the
simulator and streams values over Wi-Fi to small ESP32 boards, each of which
draws one instrument.

The first instrument is the Airbus A320 accumulator / brake pressure triple
indicator, fed by the Fenix A320 add-on. It works today on screen with live
sim data; the hardware has been ordered but not yet received.

## 2. The one rule that governs the design

**Panels display; the hub thinks.**

All simulator access, variable mapping, arithmetic and profile logic live on
the PC. A panel receives final numbers and draws them. Nothing about a
panel's appearance exists anywhere in the PC application.

The consequence, which is a hard requirement: **adding a new panel must never
require rebuilding or changing SimDeck.** A panel announces itself, says which
logical values it wants, and the hub obliges. If you find yourself adding
panel-specific code to `SimDeck.App`, stop - that is the mistake Phase 1 was
spent undoing.

## 3. Current state, honestly

### Verified working against the real simulator
- Reads live Fenix A320 data over SimConnect at frame rate
- Profile with confirmed variable names (`N_HYD_PRESSURE_BRAKE_ACCU`, `_LEFT`,
  `_RIGHT`, reported in thousands of psi)
- Discovery, streaming at a measured 30 Hz, OTA with SHA-256 verification,
  input events reaching the simulator
- Tray behaviour, launch-at-startup, crash logging
- 83 automated tests, stable across repeated runs

### Written but never run on hardware
- **Panel firmware.** `firmware/accu_panel/` is an Arduino sketch for a
  *480x480* board and predates the photographic face. Treat it as reference,
  not as the panel. It needs rewriting for the board that was actually bought.
- `firmware/SimDeckClient/` - the ESP-side protocol client. Structurally
  sound, compiled by eye only.

### Known gaps
- Which ARINC 408A size the real instrument is was never confirmed from a
  datasheet. 2ATI is assumed; the chosen board matches it.
- `SimDeck.App` cannot be compiled on Linux (WPF). Use `tools/preflight.py`
  as a partial stand-in, then build on Windows.

## 4. Build and run

Windows, .NET 8 SDK (a newer SDK is fine; the projects target net8.0).

```
build.cmd
```

Runs the tests, publishes `publish\SimDeck.exe`, and builds the installer if
Inno Setup is present. `build-log.txt` holds the output.

```
publish\SimDeck.exe            live, via SimConnect
publish\SimDeck.exe --mock     synthetic data, no simulator needed
```

Tests alone, and they run on any platform:

```
dotnet run --project tests/SimDeck.Core.Tests
```

## 5. Layout

```
src/SimDeck.Core/     engine. net8.0, no Windows dependency, fully testable
  Protocol.cs         the wire format. FROZEN - see section 7
  HubService.cs       discovery, streaming, OTA, inputs, timing
  Sources/            IDataSource; SimConnect (live) and Mock (bench)
  AircraftProfile.cs  logical name -> simulator variable mapping
  Ota/                firmware repository and per-module OTA state
  LvarCatalog.cs      finds variable names in an aircraft's own files

src/SimDeck.App/      WPF tray application. net8.0-windows
                      Devices, Variables, Firmware, Settings, Log
                      NO panel-specific code. Keep it that way.

src/SimDeck.Bench/    Phase 2, not built yet. Contains only GaugeControl.cs,
                      moved here out of the app, and the dial face.

tests/                83 checks. Plain console runner, no test framework, so
                      it needs no packages and runs anywhere.

artwork/              face extraction and geometry measurement (Python)
firmware/             ESP32 client library and the superseded 480 sketch
docs/                 architecture, protocol, decisions, hardware, status
tools/preflight.py    stands in for the compiler on non-Windows machines
lib/SimConnect.dll    native, copied beside the exe at build time
fonts/                Routed Gothic, OFL
bench-tools/          Python reference hub; same protocol, byte for byte
```

## 6. Zero NuGet dependencies

The application references no packages at all. Tray via WinForms
`NotifyIcon`, JSON via `System.Text.Json`, HTTP via a raw `TcpListener`,
theming via a `ResourceDictionary`. `nuget.config` keeps nuget.org only
because a self-contained publish downloads the .NET runtime packs.

**Please do not add packages casually.** The absence of them is why this
still builds unattended, and it was a deliberate choice (`decisions.md` #4).

## 7. Things that will bite you

**The protocol is frozen at v1.** `Protocol.cs` constants and a golden frame
are asserted by tests. Those numbers are a contract with firmware already
flashed and sitting in a cockpit. If a change is genuinely needed, bump
`Protocol.Version` and support both versions - do not edit the numbers. An
earlier 6-versus-7-byte header mismatch between two implementations would
have silently corrupted every frame on real hardware.

**`SimConnect.dll` is the native DLL only.** Never add
`Microsoft.FlightSimulator.SimConnect.dll`. It is a C++/CLI mixed-mode
assembly built against .NET Framework and .NET 8 throws
`BadImageFormatException` on load. It looks like a corrupt file; it is not.

**Windows timer resolution.** The tick loop calls `timeBeginPeriod(1)` and
measures the real sleep granularity at startup. Without that, a requested
30 Hz stream runs at 23.7 Hz, measured. There is a test that fails outside
27-33 Hz.

**WPF drops `System.IO` from implicit usings**, because
`System.Windows.Shapes.Path` collides with `System.IO.Path`. The symptom is
"Path does not exist in the current context", not an ambiguity error. Add
`using System.IO;` to any file in `SimDeck.App` that needs it.

**FSUIPC is not the way in.** A Lua bridge was built and measured: a 1766 ms
loop, of which a *1 ms sleep* took 141 ms, because FSUIPC schedules Lua
threads a couple of times a second. It has been removed. The measurements are
in `decisions.md` #8 - read them before reaching for it again.

**Artwork geometry has a single source of truth.** `artwork/build/geometry.json`
holds pivots, boss radii and piecewise scale angles. The C# constants in
`GaugeControl.cs` are *generated* from it. If you change one, regenerate the
other, or the screen and the hardware will disagree.

## 8. Next work, in order

**Phase 2 - `SimDeck.Bench`.** A separate application that *is* a panel: it
speaks the wire protocol over a real socket, displays values, fires input
events, accepts OTA, and renders a face from `geometry.json`. This lets
panels be designed, calibrated and tested with no ESP32 present, and it
exercises the hub continuously.

One design rule: the bench must go over a real loopback socket and must never
call into `HubService` directly. A shortcut there stops it testing the thing
that matters.

**Phase 3 - panel template.** A `SimDeckPanel` library owning *only* network,
values by logical name, input handling, OTA and identify - nothing about
drawing. Then a copyable template project, then the first real panel for the
1.85 inch board. Every future panel starts from that template.

An open question left for the new owner: **Arduino or ESP-IDF for the
template.** Arduino is far lower friction for panel authors and Waveshare ship
an Arduino LVGL demo for this exact board; ESP-IDF gives finer control and a
better long-term footing. The prior recommendation was Arduino.

**Phase 4 - bring-up.** Flash Waveshare's own test firmware first to prove the
board and the toolchain, then the template, then the real panel, then the
first over-the-air update.

Then: an octagonal bezel to print (63.5 mm across, four corner holes), and a
second instrument.

## 9. Hardware on order

Waveshare ESP32-S3-Touch-LCD-1.85. 360x360 round IPS, ST77916 over QSPI,
16 MB flash, 8 MB PSRAM, board 55 x 55 mm - a 2ATI cutout is 55.9 mm, so it
is nearly the instrument's own size.

Two firmware-relevant details that will waste an afternoon if missed:

- **The LCD reset line runs through a TCA9554 I/O expander**, not a GPIO. It
  must be released over I2C before the panel will initialise.
- **The partition scheme must have two app slots**, or OTA fails at
  `Update.begin()` every time. Avoid anything named "Huge APP".

The board powers on whenever 5 V is present; its PWR button is a battery
latch only, and no battery is fitted.

## 10. Working style that produced this

Worth continuing, because most of the value in this repository came from it:

- **Measure before concluding.** Every performance decision here came from a
  number, and several times the number contradicted the obvious explanation.
- **Instrument rather than guess.** The Lua dead end was only provable once
  the script timed its own sleep.
- **Write the failure into a test.** Every bug found on real hardware has a
  test that would have caught it.
- **Record the reasoning, not just the change.** `decisions.md` exists so that
  nobody undoes something that looks odd without knowing why it is odd.
- **State what is unverified.** Several things here have never run on
  hardware, and they say so.
