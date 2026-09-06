# Reading LVARs over SimConnect

## The short version

Since MSFS **Sim Update 12** (March 2023) you can add an LVAR to an ordinary
SimConnect data definition:

```cpp
SimConnect_AddToDataDefinition(h, DEF, "L:N_HYD_PRESSURE_BRAKE_ACCU", "number");
```

and request it with `SIMCONNECT_PERIOD_SIM_FRAME`. The simulator then pushes
the value whenever it changes, once per frame. No FSUIPC, no WASM module, no
Lua, no licence.

## Why the long way round exists

Before SU12 this genuinely was not possible, and the standard advice was
correct at the time: an external program could not see LVARs, so you had to
ship a WASM module into the simulator to relay them out. That is what
FSUIPC's WASM does, what MobiFlight's module does, and what the tooling
around WinWing's hardware is built on.

Most search results still describe that workaround, which is how this project
spent several rounds on an FSUIPC Lua bridge before finding the direct route.

The measurements are the argument. Lua bridge, three variables, live Fenix:

| | |
|---|---|
| loop | 1766 ms |
| reading 3 variables | 562 ms |
| writing a 1 KB file | 672 ms |
| sleeping (1 ms requested) | 141 ms |

A sleep cannot be slowed by antivirus, and a file write cannot be slowed by
WASM latency. Everything being slow at once means FSUIPC schedules its Lua
threads a couple of times a second — a ceiling nothing inside the loop can
lift. SimConnect has no equivalent: values arrive on a callback at frame rate.

## Setup

1. Install the MSFS SDK (Developer Mode → Help → SDK Installer)
2. Copy **`SimConnect.dll`** — the native one — into `lib\`
3. Run `build.cmd`

The Variables page will read `MSFS: live`.

## Do not use the managed wrapper

`Microsoft.FlightSimulator.SimConnect.dll` is a C++/CLI mixed-mode assembly
compiled against .NET Framework. .NET 8 cannot load mixed-mode assemblies
built for Framework, and throws `BadImageFormatException` as soon as the type
is touched:

```
System.BadImageFormatException:
File name: '...\Microsoft.FlightSimulator.SimConnect.dll'
```

That reads like a corrupt file or an x86/x64 mismatch. It is neither — the
assembly is simply unloadable on this runtime, and no amount of rebuilding
changes that.

SimDeck therefore P/Invokes the native `SimConnect.dll` directly. It is one
file instead of two, there is no build-time dependency at all, and a missing
DLL is reported on the Variables page rather than crashing.

## One thing SimConnect cannot do

It cannot **enumerate** LVARs — there is no "list all variables" call. You can
read any name you know, but you have to know it.

That is why the Variables page keeps **Read from aircraft files**: it pulls
names out of the aircraft's own behaviour XML. Which turns out to be the more
reliable route anyway, since FSUIPC's enumeration was missing from your build.

## Implementation notes

One data definition and one request per variable, rather than a packed struct.
Variable-length structs through the managed wrapper are awkward, a definition
each is far simpler, and a few dozen requests cost nothing.

Definition ids are never reused. Clearing and re-adding an id while the sim
still holds requests against it yields stale values that are painful to trace.

A `SIMCONNECT_RECV_EXCEPTION` almost always means a variable name that does
not exist in the loaded aircraft. It is not fatal — the other requests carry
on — so it is reported rather than thrown.
