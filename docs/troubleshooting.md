# Troubleshooting

Start with the **Variables** page. Its first line names the layer that is
failing; everything below is organised by that line.

### `MSFS: SimConnect.dll not found`
Put the native `SimConnect.dll` beside `SimDeck.exe`. It is in `lib\` and the
build copies it. Never use `Microsoft.FlightSimulator.SimConnect.dll` - .NET 8
cannot load it (`BadImageFormatException`).

### `MSFS: not connected`
The sim is not running, or not yet in a flight. SimDeck retries every 2 s.

### `MSFS: live` but the gauge says "not resolving"
The active profile's variable names do not exist in this aircraft. Variables →
Read from aircraft files, tick candidates, cycle the park brake, find the ones
that move, put them in the profile at `%LOCALAPPDATA%\SimDeck\profiles\`, then
Settings → Reload profiles.

### No profile matches
The aircraft title contains none of the profile's `match` tokens. Add one.
Longest token wins, so be specific.

### Gauge sits at zero with the sim running
Expected under pedal braking. The indicator is fed by the yellow hydraulic
system; pedals run off green. Park brake or alternate braking only.

### Panels never appear
Windows Firewall. Panels find the hub by UDP broadcast; allow SimDeck on the
private network. Also check PC and panels share a subnet - broadcast does not
cross routers.

### OTA fails immediately
Almost always the ESP32 partition scheme has one app slot. Choose one with two.
Other causes: firewall on TCP 27502; the module cannot route to the address
SimDeck advertised (multi-NIC or VPN - the hub picks the interface that routes
to the module, but a VPN can still win).

### The window never opens
`%LOCALAPPDATA%\SimDeck\crash.log`. Every unhandled exception is written there
and shown in a dialog.

### `build.cmd` fails
`build-log.txt` next to it holds the compiler output. The first line with a
code (`CS0104`, `MC3024`, `NETSDK…`) is normally the only real error.

### Old Lua bridge (`--fsuipc`)
Bridge folder is `%LOCALAPPDATA%\SimDeck\bridge\`; `status.txt` should
rewrite continuously. Expect ~1 update/s regardless - that route is a dead end
and is documented as such in `docs/decisions.md` #8.
