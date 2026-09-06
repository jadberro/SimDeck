# Status and roadmap

_As of 6 September 2026._

## Working, verified on real hardware / real sim

- SimDeck builds from `build.cmd` on Windows; installer produced by Inno Setup
- Reads live Fenix A320 data via SimConnect at frame rate
- Profile for Fenix A32X with confirmed variable names:
  `N_HYD_PRESSURE_BRAKE_ACCU / _LEFT / _RIGHT`, thousands of psi (scale 1000)
- On-screen accumulator/brake gauge with the real face artwork, calibrated
- Variables page: read names from aircraft files, watch live values
- Tray, close-to-tray, launch at startup, crash log
- 73 automated tests in `SimDeck.Core.Tests`, including a loopback
  integration run (discovery, 30 Hz rate check, OTA download + SHA-256)

## Written, not yet exercised

- **Panel firmware** for the 1.85" board - not written yet; the Arduino
  `accu_panel.ino` targets a 480 board and predates the photographic face
- OTA end-to-end with a real ESP32 (the hub side is tested; the module side
  is compiled-by-eye)
- Module inputs (`ev` messages → sim writes)

## Known gaps

- ATI size of the real instrument unconfirmed (2ATI assumed)
- `FsuipcClientSource` API unverified against the real DLL
- The gauge preview is hardcoded to one panel (by decision - see decisions #15)

## Next steps, in order

1. Board arrives → flash Waveshare test firmware → confirm screen
2. Firmware template for the 1.85" board: ESP-IDF, ST77916 QSPI, TCA9554
   reset, LVGL, `SimDeckClient` ported from Arduino APIs, face at 360,
   pointers as filled polygons with the piecewise mapping
3. First OTA round trip
4. Octagonal bezel STL, 63.5 mm with 4 corner holes
5. Second instrument
