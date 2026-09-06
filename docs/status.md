# Status and roadmap

_As of 6 September 2026._

## Phase 1 complete - PC app stabilised

- Panel-specific code removed from the app; gauge renderer moved to
  `src/SimDeck.Bench/` ready for Phase 2
- FSUIPC route removed entirely; SimConnect is the only live source
- Protocol frozen at v1, asserted by a golden-frame test
- Input path (`ev` → profile → sim write) implemented and tested
- Robustness suite: sim restart, profile reload, aircraft change, module
  power-cycle, duplicate ids, oversized subscriptions, malformed packets
- 83 tests, no flakes over repeated runs

## Working, verified on real hardware / real sim

- SimDeck builds from `build.cmd` on Windows; installer produced by Inno Setup
- Reads live Fenix A320 data via SimConnect at frame rate
- Profile for Fenix A32X with confirmed variable names:
  `N_HYD_PRESSURE_BRAKE_ACCU / _LEFT / _RIGHT`, thousands of psi (scale 1000)
- On-screen accumulator/brake gauge with the real face artwork, calibrated
- Variables page: read names from aircraft files, watch live values
- Tray, close-to-tray, launch at startup, crash log
- 83 automated tests in `SimDeck.Core.Tests`, including a loopback
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

**Phase 2 - `SimDeck.Bench`.** A separate app that *is* a panel: speaks the
protocol over a real socket, shows values, fires input events, accepts OTA,
and renders a face from `geometry.json`. Lets panels be built and calibrated
with no ESP32 present.

**Phase 3 - panel template.** `SimDeckPanel` library (network, values,
inputs, OTA, identify - nothing else), a copyable template project, then the
first real panel for the 1.85" board.

**Phase 4 - bring-up.** Waveshare test firmware → template → real panel →
first OTA.

Then: octagonal bezel STL (63.5 mm, 4 corner holes), second instrument.
