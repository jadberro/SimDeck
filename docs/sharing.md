# Sharing a build

## Short answer

Give them **`installer\Output\SimDeck-1.0.0-setup.exe`**. It carries whatever
data source was compiled in, including the SimConnect DLLs. They need the
simulator and nothing else — no SDK, no FSUIPC, no `lib\` folder, no build
tools.

The `lib\` folder is a **build-time** requirement, not a runtime one.

## What ends up in the build

`SimConnect.dll` is bundled in `lib\` and copied beside the executable, so a
build works on any machine with MSFS. Nothing else is required — no SDK, no
FSUIPC, no licence, no scripts.

The old FSUIPC Lua route still exists behind `--fsuipc` for comparison, but
nobody should need it.

## Redistribution

`SimConnect.dll` comes from the MSFS SDK, and bundling it is common practice
across MSFS add-ons. **Check the SDK licence terms before distributing
publicly** — sending a build to a friend is a different matter from putting it
on flightsim.to, and I am not in a position to give you a legal answer.

## What they will need to do

1. Run the installer
2. Allow the Windows firewall prompt on first run — panels are found by
   network broadcast, so blocking it means nothing ever connects
3. Nothing else. Profiles are seeded on first run.

If they fly a different aircraft, they will need a profile for it. The
Variables page → **Read from aircraft files** works for any add-on, not just
Fenix.
