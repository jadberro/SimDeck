namespace SimDeck.Core.Sources;

/// <summary>
/// Chooses how to talk to the simulator.
///
/// SimConnect is the only live route. Since MSFS Sim Update 12 an LVAR can be
/// requested like any other variable, so nothing else is needed - no FSUIPC,
/// no licence, no WASM module, no bridge process.
///
/// An FSUIPC Lua bridge was built, measured and removed: it topped out near
/// one update per second because FSUIPC schedules Lua threads a couple of
/// times a second, which no amount of tuning inside the loop could lift. See
/// docs/decisions.md #8 for the measurements before reaching for it again.
/// </summary>
public static class SourceFactory
{
    public static string PreferredName => "SimConnect";

    public static IDataSource CreateLive() => new SimConnectSource();

    public static IDataSource Create(bool mock) =>
        mock ? new MockSource() : CreateLive();
}
