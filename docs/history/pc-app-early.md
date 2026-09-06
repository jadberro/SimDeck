> **Superseded.** An early description of the C# app, written before the
> SimConnect source replaced FSUIPC. Kept for the reasoning it records.
> For the current system see [`docs/architecture.md`](../architecture.md).

# SimDeck (C#)

The shipping application. One .NET service for every module in the cockpit.

```
MSFS ── FSUIPC ──▶ HubService ──udp──▶ accu-01
                       │  ├──────────▶ fcu-01
                       │  └──────────▶ ecam-01
                       ├── profiles/*.json
                       └── :27502 firmware images
```

## Why C# rather than Python

Everything on a Windows sim rig already speaks .NET. MobiFlight is C#.
FSUIPC ships an official .NET client with LVAR support. The SimConnect
managed wrapper is C#. Going native removes the Lua bridge entirely and
reads LVARs in-process.

It also packages properly: one self-contained `.exe`, a real installer, a
tray icon that works, and a 30Hz loop that is not fighting the GIL.

**The firmware did not change to accommodate any of this.** The wire
protocol is the contract, and C# emits byte-identical frames.

## Zero dependencies

No NuGet packages at all. Tray is `System.Windows.Forms.NotifyIcon`, JSON is
`System.Text.Json`, HTTP is `HttpListener`, theming is a `ResourceDictionary`.
Nothing to restore, nothing to go stale, nothing to break a build in two
years. `nuget.config` clears the package sources so this stays true.

## Layout

| Project | What |
|---|---|
| `SimDeck.Core` | Protocol, hub, sources, OTA. `net8.0`, cross-platform. |
| `SimDeck.App` | WPF window and tray. `net8.0-windows`. |
| `SimDeck.Core.Tests` | Console runner, no test framework needed. |

Core is deliberately platform-neutral: it builds and its tests run on Linux,
which is how the protocol got verified against the Python implementation.

## Build

```
build.cmd
```

Runs the tests, then publishes `publish\SimDeck.exe`. For the installer,
install Inno Setup and run `iscc installer\SimDeck.iss`.

```
dotnet run --project tests\SimDeck.Core.Tests
```

35 checks covering frame encoding, version comparison, profile matching,
value resolution, the firmware repository, and a full integration pass that
drives a real `HubService` with a simulated module on the loopback:
discovery, streaming rate, OTA offer, HTTP download and SHA-256 verification.

## Running

```
SimDeck.exe              live, via FSUIPC
SimDeck.exe --mock       bench mode, sim closed
SimDeck.exe --minimised  start hidden in the tray
```

Data lives in `%LOCALAPPDATA%\SimDeck` — profiles, firmware images, the LVAR
watchlist. Shipped profiles are seeded there on first run and never
overwritten afterwards, so your edits survive an upgrade.

Closing the window hides it. The hub keeps streaming and the only real exit
is Quit from the tray, because closing a window by reflex should not take
the gauges down mid-flight.

## What the installer does

- Per-user install by default, so no UAC prompt.
- Optional startup shortcut with `--minimised`.
- If installed as admin, adds an inbound firewall rule for the private
  profile. Modules find the hub by UDP broadcast, so inbound must be allowed.
  Without admin, Windows prompts on first run instead.

No URL ACL is required. The firmware server was originally built on
`HttpListener`, which sits on HTTP.sys and refuses to bind any prefix without
a `netsh` reservation — so on a normal user account it never started and
every download was refused. It is a plain `TcpListener` now, serving one
route, which removes the reservation, the elevation, and a whole class of
confusing failure.

## FSUIPC: two ways in

`FsuipcLuaSource` is the default and needs nothing but FSUIPC itself — the
Lua script streams LVARs over loopback UDP. Proven and dependency-free.

`FsuipcClientSource` reads LVARs directly through the FSUIPC .NET client and
deletes the Lua script from the picture. Not compiled by default because
`FSUIPCClient.dll` comes from the FSUIPC7 SDK and is not redistributable:

1. copy `FSUIPCClient.dll` beside the solution
2. add a `Reference` to it in `SimDeck.Core.csproj`
3. build with `-p:DefineConstants=FSUIPC_CLIENT`

## Timing

The tick loop runs on its own above-normal thread and uses a hybrid wait:
`Thread.Sleep(1)` while far from the next send, then a spin for the last
stretch.

`Thread.Sleep` rounds up to the system timer resolution, about 15.6ms on
Windows by default. With a 33ms send period that overshoots to roughly 46ms,
and a requested 30Hz stream actually runs at 22Hz. Measured on real
hardware before the fix: 23.7Hz.

Two defences, because one was not enough:

1. `timeBeginPeriod(1)` on Windows, asking for 1ms resolution. This is what
   audio and MIDI applications do for the same reason.
2. The spin threshold is *measured* at startup rather than assumed. The loop
   times how long `Thread.Sleep(1)` actually takes and spins for at least
   that long before each deadline. If the timer request is ignored, the rate
   still holds — it costs CPU instead of accuracy, which is the right way
   round.

The integration test measures the achieved rate and fails outside 27–33Hz.
