# SimDeck by Jad Berro

Drives home-cockpit instrument panels from Microsoft Flight Simulator 2024.
One Windows service reads the sim and streams values over Wi-Fi to small
ESP32 panels that each draw one instrument. First instrument: the Airbus
A320 accumulator / brake pressure triple indicator, from the Fenix A320.

**New here? Read [`START-HERE.md`](START-HERE.md).** It is the only document
you need to build, install and connect it.

## Documentation

| | |
|---|---|
| [`START-HERE.md`](START-HERE.md) | build, install, connect, find variables |
| [`docs/architecture.md`](docs/architecture.md) | how the pieces fit, and the one rule that matters |
| [`docs/protocol.md`](docs/protocol.md) | the wire format, byte by byte |
| [`docs/decisions.md`](docs/decisions.md) | why things are the way they are - **read before changing anything odd** |
| [`docs/hardware.md`](docs/hardware.md) | the panel board, its wiring, and instrument dimensions |
| [`docs/artwork.md`](docs/artwork.md) | how the face and geometry are produced |
| [`docs/simconnect.md`](docs/simconnect.md) | reading LVARs directly, and the measurements that led there |
| [`docs/sharing.md`](docs/sharing.md) | giving a build to someone else |
| [`docs/troubleshooting.md`](docs/troubleshooting.md) | by symptom |
| [`docs/status.md`](docs/status.md) | what works, what does not, what is next |
| `docs/history/` | superseded designs, kept for the reasoning they record |

## Layout

```
src/        SimDeck.Core (engine, cross-platform) and SimDeck.App (WPF)
tests/      83 checks, no test framework needed: dotnet run --project tests/SimDeck.Core.Tests
installer/  Inno Setup script
firmware/   ESP32 client library and the first panel sketch
artwork/    face extraction, geometry measurement, calibration renders
fonts/      Routed Gothic (OFL)
lib/        native SimConnect.dll, copied beside the exe at build
bench-tools/ Python reference hub, same protocol byte-for-byte
tools/      helper scripts
```

## Licence

Code: MIT (see `LICENSE`). Routed Gothic: SIL OFL 1.1 (`fonts/LICENSE.md`).
`SimConnect.dll` is Microsoft's, from the MSFS SDK - check its terms before
public redistribution. The dial artwork is the author's.
