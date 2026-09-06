# SimDeck

One PC-side service for every module in the cockpit, plus module #1: the
A320 accumulator and brake pressure triple indicator.

```
MSFS ─ FSUIPC7 ─ simdeck.lua ──udp──▶ hub.py ──udp──▶ accu-01  (built)
                                        │  ├──────▶ fcu-01    (later)
                                        │  └──────▶ ecam-01   (later)
                                        ├── profiles/*.json
                                        ├── firmware_bin/  (OTA images)
                                        └── :27502 web UI
```

## The four ideas worth keeping

**Logical names.** Firmware subscribes to `brake.accum_psi`, never to an
LVAR. A profile maps logical to raw per aircraft. Swap Fenix for iniBuilds
by editing one JSON file — no reflashing.

**Self-describing modules.** A module broadcasts its id, type, firmware
version and wanted values on boot. The hub assigns slots and streams.
Adding module #7 means flashing it; nothing on the PC changes.

**Pluggable source.** `sources/base.py` is the interface. Mock and FSUIPC
ship; X-Plane or SimConnect drop in beside them.

**Generated geometry.** `art/make_dial.py` emits the dial artwork *and*
`gauge_geometry.h`. The painted scale and the needle angles cannot drift
apart because they are the same numbers.

## Run it

Bench test, sim closed — everything works except real data:

```
cd simdeck
python hub.py --source mock
```

Then open <http://localhost:27502>. The mock cycles the park brake every
20s so all three needles move.

Live:

```
python hub.py --source fsuipc
```

1. Copy `simdeck/lua/simdeck.lua` into your FSUIPC7 folder.
2. Edit `WATCHLIST` at the top to the absolute path of
   `simdeck/lua/simdeck_lvars.txt`.
3. In `FSUIPC7.ini` under `[Auto]`, add `1=Lua simdeck`.

The hub writes the watchlist itself from whatever modules are connected,
so you never edit LVAR names in Lua.

## Desktop app

```
pip install -r requirements.txt
cd simdeck
python app.py --source fsuipc
```

A native window wrapping the dashboard, plus an icon in the Windows
notification area.

Closing the window **hides** it. The hub keeps streaming to your panels and
the only real exit is Quit from the tray menu — closing a window by reflex
should not take the gauges down mid-flight.

The tray icon is drawn at startup rather than shipped as a file. It goes
red when the data source stops answering, and the tooltip carries the
module count, so you can check the sim link without opening anything.

`--minimised` starts hidden. Put a shortcut to that in `shell:startup` and
it comes up with Windows.

No pywebview, or no Edge WebView2 runtime, and it falls back to your
default browser and runs tray-only. Same close-to-tray behaviour, since the
tray is what holds the process alive.

`hub.py` still runs headless if you would rather have a console window.

## Before it works: find the real LVARs

`profiles/fenix_a32x.json` contains **placeholder names**. Replace them.

- `SimObjects\Airplanes\FNX_32X\attachments\fnx\Part_Interior_Cockpit\model\Cockpit_Behavior.xml`
  — search the `<VAR_NAME>` attributes. Fenix documents this file as the
  place to find variables for external binding.
- Or use SPAD.next / Axis and Ohs / MobiFlight's LVAR browser, filter on
  `FNX320`, and watch which values move as you cycle the park brake.

Check units. If Fenix reports bar, set `scale` to 14.5038 for psi.

Expect the gauge to sit still when you press the pedals. On the A320 this
indicator is fed by the yellow system; the pedals run off green. Movement
on park brake or alternate braking only. That is correct, not a bug.

## Firmware updates over the air

The hub hosts firmware and offers it to modules; the module does the fetch.
A stalled download is the module's problem to retry and never blocks the
hub.

1. Build the sketch (Arduino IDE, or the hub's `/api/build/<type>` if
   `arduino-cli` is on PATH).
2. Publish the `.bin` into `firmware_bin/` — the manifest records version,
   size and SHA-256.
3. Any module of that type reporting an older version gets offered the
   update on its next HELLO. Or hit **Push firmware** in the web UI to
   force it.

The module streams the image straight into the OTA partition, hashes as it
goes, and **verifies SHA-256 before committing**. A corrupt image that got
written anyway would need a USB cable to recover.

**Partition scheme must have two app slots.** Any scheme whose name
mentions OTA, or "Minimal SPIFFS". The "Huge APP" schemes have one app
partition and `Update.begin()` fails every time.

Version compare is numeric (`1.10.0` > `1.9.0`), so date stamps like
`2026.09.05` work fine as versions.

## Artwork pipeline

```
cd art
python make_dial.py       # SVG + PNG + gauge_geometry.h
python preview.py 3 0 2.4 # needles at chosen values, to check angles
python to_lvgl.py         # PNG -> LVGL C arrays (RGB565)
python verify_render.py   # decode the real C arrays and composite them
```

`verify_render.py` is the one that earns its keep — it decodes the actual
byte arrays the firmware will use and places the needles with the same
pivots the sketch uses. It caught a pivot error that would have made every
needle over-read by 20%.

Copy `build/gauge_geometry.h` and `build/img_*.c` next to the sketch after
any change. Dial face is 461 KB of flash; needles are a few KB.

To adjust the dial, edit the `ACCUM` / `BRAKE_L` / `BRAKE_R` dicts at the
top of `make_dial.py` and re-run. Everything downstream follows.

## Firmware

`firmware/SimDeckClient/` is the reusable protocol client — copy it into
every future module unchanged. It handles discovery, streaming, inputs,
identify and OTA.

`firmware/accu_panel/` is module #1. Still to do there:

- Board display bring-up in `setup()` — panel init, `lv_init()`, LVGL tick
  and flush hooks, from your board's BSP. Everything else is done.
- Set `WIFI_SSID` / `WIFI_PASS`.
- Tune the six angle constants against a straight-on photo if the needles
  do not sit right.

Targets LVGL 8 with `LV_COLOR_DEPTH 16`. For LVGL 9 the pixel data is
identical; only the descriptor struct changed.

## Protocol

Control plane is JSON on udp/27500 (hub) and udp/27501 (modules):
`hello` → `welcome`, then `ping`/`pong`, `ev` for inputs, `ota` and
`ota_status` for updates, `identify` for the locate button.

Data plane is a packed binary frame to udp/27501:

```
u8 magic(0x5A) | u8 ver | u16 seq | u8 count | u8 flags | f32 x count
```

Unresolved values are sent as NaN and the firmware holds its last good
reading. `flags` bit 0 = source alive, bit 1 = profile matched. When the
link dies the needles drive to zero rather than freezing mid-scale, which
would read as a working instrument showing a real pressure.

Out-of-order datagrams are dropped by sequence number. Nothing is
retransmitted — at 30Hz the next frame is 33ms away.

## Ports

| Port  | Proto | Use |
|-------|-------|-----|
| 27500 | UDP   | hub control plane |
| 27501 | UDP   | module control + data |
| 27502 | TCP   | web UI and firmware hosting |
| 27510 | UDP   | FSUIPC Lua → hub (loopback) |
| 27511 | UDP   | hub → FSUIPC Lua (loopback) |
