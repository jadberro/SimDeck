# SimDeck

Software that drives home-cockpit instrument panels from Microsoft Flight
Simulator. One PC application; every panel is a small ESP32 board that
announces itself over Wi-Fi.

Read this file. Everything else is reference.

---

## What you need

| For | Get |
|---|---|
| Building the PC app | [.NET 8 SDK](https://dotnet.microsoft.com/download) — free |
| Making an installer | [Inno Setup 6](https://jrsoftware.org/isdl.php) — free, optional |
| Flashing a panel | [Arduino IDE 2](https://www.arduino.cc/en/software) + ESP32 board support |
| Live sim data | FSUIPC7 (you already have this if you fly the Fenix) |

Windows only. The PC app is WPF.

---

## 1. Build the PC app

Double-click **`build.cmd`**.

It runs the tests, builds `publish\SimDeck.exe`, and — if Inno Setup is
installed — writes `installer\Output\SimDeck-1.0.0-setup.exe`.

First run downloads the .NET runtime packs, so give it a few minutes.

If it stops with errors, copy the **first** error line and send it to me.
WPF errors cascade; the first one is normally the only real one.

---

## 2. Prove it works, with no hardware

```
publish\SimDeck.exe --mock
```

The window opens. There will be no devices, because none exist yet — that is
correct. Mock mode invents a fake A320 brake system so you can develop
without the sim running.

To see a device appear, use the bench tool in `bench-tools\` — it can
pretend to be a panel. That is also how you test firmware changes without a
board on your desk.

---

## 3. Install it properly

Run **`installer\Output\SimDeck-1.0.0-setup.exe`**.

Admin is optional. If you allow it, the installer adds a firewall rule up
front; if you do not, Windows prompts you to allow SimDeck on first run
instead. Either way everything works, firmware updates included.

Say **yes** to that firewall prompt when it appears. Panels find the PC by
network broadcast, so blocking it means nothing ever connects.

The installer offers a "start with Windows" tick box. Take it — SimDeck
lives in the notification area and should be running before you load a
flight.

**Closing the window hides it** by default, and the app keeps feeding your
panels; quit from the tray icon. Settings lets you change that so close
quits instead, and lets you launch SimDeck with Windows.

---

## 4. Connect it to the sim

**FSUIPC7 must be licensed.** Lua scripting is a paid feature. Open
`FSUIPC7.log` — if it says `FSUIPC7 not user registered`, this route will not
work until you buy a licence or take the trial from fsuipc.com.

Then run **`tools\install-lua.cmd`**.

It finds your FSUIPC7 folder (asking the running process first, then the
usual places, then you), copies the bridge script in, backs up
`FSUIPC7.ini`, and adds `1=Lua simdeck` to the `[Auto]` section. It never
overwrites the ini without a timestamped backup beside it.

Then:

1. Restart FSUIPC7
2. **Load your aircraft and sit in the cockpit, ready to fly.** Auto scripts
   do not start until a flight is loaded — this catches people out.
3. Open SimDeck → Variables. It should say `bridge: live`.

If it does not, you can check by hand. The bridge is a plain folder:

    %LOCALAPPDATA%\SimDeck\bridge

Paste that into Explorer. You should see `status.txt` being rewritten
twenty times a second. Open it — it shows the aircraft name and the current
values as plain text:

    #SEQ 4213
    #AC Fenix A320 IAE
    #N 3
    FNX320_BRAKE_ACCU_PRESS=2947.0000
    #END

No `status.txt` at all means the script is not running. `FSUIPC7.log` will
contain `SimDeck lua started` if it loaded.

## 5. Find the real variable names

SimDeck → Variables. There are three ways to get a list, in order of
reliability:

**Read from aircraft files** — the one to use. Reads the Fenix package's own
behaviour XML and pulls every variable name out of it. Needs no FSUIPC
feature at all, so it works on any build. It finds the package by reading
`UserCfg.opt` for wherever your Community folder actually is; **Browse…**
lets you point at `Cockpit_Behavior.xml` yourself if that fails.

**Scan sim** — asks FSUIPC to enumerate them live. Faster, but depends on
`ipc.getLvarList`, which not every FSUIPC build has. If it returns nothing,
that is why — use the file route instead.

**Watch** — type a name you already suspect and watch it directly. Reads
`waiting` if the name does not exist, which is a quick way to test a guess.

Brake and accumulator candidates are listed first automatically. Tick a few
and watch the live values while you set and release the parking brake. The
ones that move are the ones you want.

Put those names into `%LOCALAPPDATA%\SimDeck\profiles\fenix_a32x.json`,
then Settings → Reload profiles.

Check the units. If Fenix reports bar, set `"scale": 14.5038` to get PSI.

Expect the gauge to sit still when you press the pedals. On the A320 this
indicator is fed by the yellow system; the pedals run off green. Movement on
park brake or alternate braking only. That is correct, not a bug.

## 6. Build the gauge panel

Hardware: an ESP32-S3 with a 2.1 inch 480×480 round LCD (Waveshare
ESP32-S3-Touch-LCD-2.1 or equivalent).

1. Open `firmware\accu_panel\accu_panel.ino` in the Arduino IDE.
2. Set `WIFI_SSID` and `WIFI_PASS` near the top.
3. Add your board's display bring-up in `setup()` where the comment says so.
   Everything else is written.
4. **Choose a partition scheme with two app slots** — any option whose name
   mentions OTA, or "Minimal SPIFFS". The "Huge APP" schemes have one app
   partition and firmware updates will fail every time.
5. Flash over USB, once. After that it updates over Wi-Fi.

It will appear in SimDeck within a few seconds of powering on. Nothing to
configure on the PC.

---

## 7. Later: updating panels over the air

1. Arduino IDE: **Sketch → Export compiled binary**
2. SimDeck → Firmware → **Add image**, and pick **`accu_panel.ino.bin`**
3. Confirm the type and version it shows you
4. Any panel of that type on an older version is offered the update

**Pick the plain `.ino.bin`.** The export folder also contains
`.merged.bin`, `.bootloader.bin` and `.partitions.bin`. Only the plain one
is a valid over-the-air image — the others start at flash offset 0 and will
not boot from an app partition. SimDeck refuses them and explains why, so
you cannot pick the wrong one by accident.

No version in the filename is fine: SimDeck date-stamps it, e.g.
`2026.09.05`. Version comparison is numeric, so date stamps order correctly
against each other. If you prefer explicit versions, name the file
`accu_panel_1.1.0.bin` and it will use `1.1.0` instead.

**There is no prebuilt `.bin` in this download.** The sketch needs your
Wi-Fi credentials and your board's display bring-up, so the first flash has
to be yours, over USB. Everything after that is over the air.

The panel verifies a SHA-256 hash before committing, so a corrupted download
cannot brick it.

---

## What is in this folder

| Folder | What |
|---|---|
| `src\` | The PC application. `SimDeck.Core` is the engine, `SimDeck.App` is the window. |
| `tests\` | 73 automated checks. `build.cmd` runs them. |
| `installer\` | Inno Setup script. |
| `firmware\` | ESP32 code. `SimDeckClient` is reusable by every future panel. |
| `artwork\` | Generates the dial face and the matching firmware geometry. |
| `fonts\` | Routed Gothic (OFL) — the MS33558 / Gorton engraving lettering. |
| `bench-tools\` | Python version of the hub, and the FSUIPC bridge script. |
| `tools\` | `install-lua.cmd` — sets up the FSUIPC bridge for you. |
| `docs\` | Deeper reference on the protocol and design decisions. |

---

## How it reads the sim

SimConnect, directly. Since MSFS Sim Update 12 an LVAR can be requested like
any other variable, and the simulator pushes values at frame rate — no
FSUIPC, no licence, no Lua, no file on disk.

`lib\SimConnect.dll` is already bundled, so there is nothing to install.
Details in `docs/simconnect.md`.

Do not add `Microsoft.FlightSimulator.SimConnect.dll` — the managed wrapper
is a .NET Framework mixed-mode assembly that .NET 8 cannot load, and it will
crash on startup. SimDeck calls the native DLL directly and does not need it.

You can tell which route is running from the Variables page: `MSFS: live` is
SimConnect, `bridge: live` is the old Lua fallback.

Note that SimConnect cannot *list* variables — only read ones you name. That
is what **Read from aircraft files** is for, and it is the more reliable route
regardless.

## If the needles lag

The Variables page shows two rates:

    bridge: live · 30 writes/s · 6 value changes/s · polling 3 variable(s)

**writes/s** is the bridge, our side. **value changes/s** is how often the
numbers actually move, which is FSUIPC's side.

If writes are fast and changes are slow, the ceiling is FSUIPC, not SimDeck,
and no amount of tuning here will help. Fix it in `FSUIPC_WASM.ini`:

1. Find the WASM **persistence** folder — the FSUIPC Advanced User Guide
   gives the path. **Do not edit the copy in your Community folder**; that
   one gets overwritten and is not the one being read.
2. Raise `LvarUpdateFrequency`. The default is low, around 6 per second.
3. Restart the simulator.

The file is commented, so check the allowed range before setting it high.
Asking for more than the WASM module can deliver is a good way to make it
unstable, and a crashed WASM module stops lvars updating altogether.

If both rates are high and it still feels sluggish, that is the smoothing
rather than the data — the needle has a deliberate 0.2 to 0.3 second time
constant so it moves like a real instrument instead of snapping.

## Sharing it with someone

Give them the installer. It carries whatever data source was compiled in,
including the SimConnect DLLs — they need the simulator and nothing else. The
`lib\` folder is a build-time requirement, not a runtime one.

Build with the SimConnect DLLs present before you share, or they inherit the
FSUIPC route and have to buy a licence. `build.cmd` prints which one it
compiled. See `docs/sharing.md`.

## If it will not start

SimDeck writes any unhandled failure to:

    %LOCALAPPDATA%\SimDeck\crash.log

and shows it in a message box rather than closing silently. Send me that
file and the failure is usually a one-line fix.

If nothing appears at all, the build did not produce an executable — run
`build.cmd` and send `build-log.txt` instead.

## Honest status

**Tested and working:** the engine, the protocol, firmware updates, profile
matching, timing. 73 automated checks, including a full run where a
simulated panel connects, streams at 30Hz, and completes an update.

**Written but never compiled:** the window itself. I had no Windows machine,
and WPF cannot be built anywhere else. Expect the first build to need a fix
or two. The engine underneath it is proven.

**Not done:** the real Fenix variable names (step 4), your board's display
bring-up (step 5), and the dial proportions are estimated from a photo
rather than measured.
