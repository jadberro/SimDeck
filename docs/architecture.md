# Architecture

## The one-sentence version

Panels display; the hub thinks.

SimDeck is a Windows service that reads the simulator, maps what it reads onto
stable *logical names*, and streams those values to hardware panels over UDP.
Every panel is a small self-contained device that announces what it wants and
draws what it receives. Nothing about a panel's appearance lives in the hub.

```
                 ┌──────────────────────── SimDeck (PC) ────────────────────────┐
                 │                                                               │
 MSFS 2024 ──SimConnect──▶ IDataSource ──▶ Profile ──▶ HubService ──UDP 30 Hz──▶│──▶ accu-01
 (Fenix A320)   L: vars    raw names       logical      slots, frames,          │──▶ fcu-01
                                           names        OTA, discovery          │──▶ ...
                 │                                                               │
                 │   FirmwareRepo + FirmwareServer (TCP :27502) ◀──── OTA pulls ─┤
                 └───────────────────────────────────────────────────────────────┘
```

## Layers

### 1. Data source (`SimDeck.Core/Sources`)

Anything that can supply named numeric values. The hub never knows which one
it is talking to.

| Source | Status | Notes |
|---|---|---|
| `SimConnectSource` | **the live route** | Reads LVARs directly. Native P/Invoke into `SimConnect.dll`; no managed wrapper. Values pushed by the sim per frame. |
| `MockSource` | `--mock` | Synthetic A320 brake cycle for bench work. |

The FSUIPC Lua bridge was built, measured and removed - see `docs/decisions.md`
#8 for the numbers before considering it again.

Raw names are the simulator's (`N_HYD_PRESSURE_BRAKE_ACCU`). Nothing else in
the system ever sees them.

### 2. Profiles (`profiles/*.json`)

Map logical names to raw names, per aircraft, with scale/offset/clamp:

```json
"brake.accum_psi": { "name": "N_HYD_PRESSURE_BRAKE_ACCU", "scale": 1000.0, "clamp": [0, 4000] }
```

A profile is chosen by matching the aircraft title. Longest matching token
wins, so `"fenix a320"` beats `"a320"`. Switching aircraft add-on is a new
JSON file; nothing else changes.

### 3. Hub (`HubService`)

- Listens on UDP 27500 for module control messages (JSON).
- Assigns each module's subscriptions to slot indices, replies `welcome`.
- Polls the source at 60 Hz, resolves each module's logical names, streams a
  packed binary frame to each module at its requested rate (max 60 Hz).
- Tracks module liveness (8 s timeout), inputs (module → sim writes), and OTA.
- Runs the tick loop on a dedicated thread with `timeBeginPeriod(1)` and a
  measured spin threshold, because `Thread.Sleep` on Windows otherwise rounds
  to ~15.6 ms and a 30 Hz stream becomes 22 Hz.

### 4. Panels (firmware)

Self-contained. A panel knows its own face, its own geometry, and the logical
names it wants. `SimDeckClient` (C++) handles discovery, frames, and OTA and
is shared by every panel unchanged. See `firmware/`.

### 5. Desktop app (`SimDeck.App`)

WPF window over the hub: devices, variables (discovery and probing), firmware
repository, settings, log. **It contains no panel-specific code.** Panel
rendering and calibration live in `SimDeck.Bench` (see `docs/status.md`).

## Key design decisions

See `docs/decisions.md` for the full log. The ones that shape everything:

- **Logical names** decouple firmware from any specific add-on.
- **Self-describing modules** mean the PC never needs configuring for a new panel.
- **Explicit 8-byte frame header** - not derived from a struct format, after a
  6-vs-7 byte mismatch between implementations was found.
- **SimConnect direct**, not FSUIPC - see `docs/simconnect.md` for the measurements.
- **Raw socket for the firmware server**, not `HttpListener` - HTTP.sys needs a
  URL ACL, which needs admin.
- **Zero NuGet dependencies** in the PC app - nothing to restore, nothing to rot.
- **Panels are not modelled in the hub** - each is its own firmware. Adding one
  never requires rebuilding SimDeck.

## Ports

| Port | Proto | Purpose |
|---|---|---|
| 27500 | UDP | hub control plane (in) |
| 27501 | UDP | module control + data (in, on the module) |
| 27502 | TCP | firmware images served to modules |

## Data folder

`%LOCALAPPDATA%\SimDeck\` - profiles (seeded on first run, never overwritten),
firmware images + manifest, settings.json, crash.log.
