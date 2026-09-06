# Wire protocol

Byte-identical across `src/SimDeck.Core/Protocol.cs`,
`bench-tools/protocol.py` and `firmware/SimDeckClient/SimDeckClient.h`.
The test suite encodes a frame in C# and compares the hex against Python.

## Control plane - JSON over UDP

Module → hub on **27500**. Hub → module on **27501**. Small hand-built JSON;
an ESP32 constructs these with string concatenation.

### hello (module → hub, broadcast until the hub is known)

```json
{"t":"hello","id":"accu-01","type":"accu_panel","name":"Accumulator/Brake Panel",
 "fw":"1.0.0","rate":30,"sub":["brake.accum_psi","brake.left_psi","brake.right_psi"]}
```

- `id` - stable, unique per board; identifies it across reboots
- `type` - firmware manifest key; all boards of one type share a firmware image
- `fw` - version; the hub offers an OTA only if the manifest is strictly newer
- `sub` - logical names, **in slot order** (max 16)

Re-sent every 3 s until linked, then every 15 s. Cheap, and it makes hub
restarts and module reboots both self-heal.

### welcome (hub → module)

```json
{"t":"welcome","hub":1,"slots":{"brake.accum_psi":0,...},"rate":30,"unknown":[]}
```

`unknown` lists subscribed names the current profile cannot resolve.

### ping / pong - every 2 s from the module; 8 s silence drops the module.

### ev (module → hub) - an input changed

```json
{"t":"ev","id":"accu-01","in":"test_btn","v":1}
```

The hub looks `in` up in the profile's `inputs` map and writes to the sim.

### ota (hub → module)

```json
{"t":"ota","url":"http://192.168.1.10:27502/fw/accu_panel_1.2.0.bin",
 "ver":"1.2.0","sha256":"...","size":123456}
```

### ota_status (module → hub) - `state` ∈ start, progress (`pct`), ok, fail (`err`)

### identify (hub → module) - flash something visible for a few seconds.

## Data plane - binary over UDP, hub → module on 27501

```
offset  size  field
0       1     magic      0x5A
1       1     version    1
2       2     seq        uint16 little-endian, wraps
4       1     count      number of float32 values following
5       1     flags      bit0 source alive, bit1 profile matched
6       2     reserved   0
8       4*n   values     float32 little-endian, slot order
```

**Header is exactly 8 bytes.** The two reserved bytes align the payload and
make the length an explicit constant. An earlier build packed 6 in Python
while the firmware parsed 7; every frame would have been misread on hardware.
Never derive this from a struct format string.

Rules:
- Unresolvable values are sent as **NaN**; firmware holds its last good reading.
- Out-of-order datagrams are dropped by sequence number (signed 16-bit diff).
- Nothing is retransmitted. At 30 Hz the next frame is 33 ms away.
- If `flags` bit0 is clear, or frames stop for 1.5 s, firmware drives pointers
  to zero rather than freezing mid-scale - a frozen needle reads as pressure.

## OTA

The hub never pushes bytes; it hands the module a URL and the module pulls
over plain HTTP from the hub's `FirmwareServer` (raw TCP, one route,
`GET /fw/<name>.bin`, file name only - no path traversal). The module streams
into the OTA partition, hashing as it goes, and **verifies SHA-256 before
committing**. Partition scheme must have two app slots.
