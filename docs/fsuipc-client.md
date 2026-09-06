# Using the FSUIPC .NET client

## Why

The Lua bridge works, but it is slow, and not for a fixable reason. Measured
on a live Fenix A320 with three variables:

| | |
|---|---|
| loop | 1766 ms |
| reading 3 variables | 562 ms |
| writing a 1 KB file | 672 ms |
| sleeping (1 ms requested) | 141 ms |

A sleep cannot be slowed by antivirus, and a file write cannot be slowed by
WASM latency. Every category being slow at once — including the sleep — means
FSUIPC schedules its Lua threads only a couple of times a second. Nothing
inside the loop can be tuned around that.

The .NET client removes the Lua thread, the file, and the process boundary.
FSUIPC's WAPI keeps a local cache of lvar values that the WASM module updates,
so reading one is a memory read rather than a round trip.

## Setup

1. Download the **FSUIPC SDK** from fsuipc.com (free; you already need a
   licence for FSUIPC itself, but not for the SDK).
2. Find `FSUIPCClient.dll` in it — the .NET/C# client by Paul Henty.
3. Copy it into the `lib\` folder beside `SimDeck.sln`.
4. Run `build.cmd`.

That is all. The build detects the DLL and compiles the client source in; no
flags, no edits. Without it, everything still builds and uses the Lua bridge.

The Variables page will show `FSUIPC: live` rather than `bridge: live`, which
is how you can tell which route is running.

## Why it is not just included

`FSUIPCClient.dll` is not redistributable, so it cannot ship in this repo.
That is the only reason the Lua bridge still exists.

## If the API does not match

The client's lvar support has changed across versions. If the build fails in
`FsuipcClientSource.cs`, the likely candidates are `ReadLVar`, `WriteLVar` and
`GetLvarList` — check the API reference bundled with the SDK and adjust those
three calls. Everything else in that file is ordinary C#.
