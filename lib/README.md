# lib

`SimConnect.dll` is here already — the native x64 one from the MSFS SDK,
verified as unmanaged and exporting the seven functions SimDeck calls.

The build copies it next to `SimDeck.exe`, and the installer includes it, so
anyone you share a build with needs the simulator and nothing else.

## Do NOT add the managed wrapper

`Microsoft.FlightSimulator.SimConnect.dll` is a C++/CLI mixed-mode assembly
built against .NET Framework. .NET 8 cannot load it at all — it throws
`BadImageFormatException` the moment the type is touched, which reads like a
corrupt or wrong-architecture file but is neither.

SimDeck P/Invokes the native DLL directly. The wrapper is unnecessary and, if
present, is the cause of that crash.

## Replacing it

If a future MSFS update needs a newer one, copy it from
`MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll` over the top. Nothing else
changes — there is no build-time dependency, only this runtime file.
