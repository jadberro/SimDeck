namespace SimDeck.Core.Sources;

/// <summary>
/// Picks the best available way to talk to FSUIPC.
///
/// The .NET client is used when FSUIPCClient.dll was present at build time.
/// It is strictly better than the Lua bridge - no second process, no file, no
/// scheduling ceiling - but the DLL is not redistributable, so the Lua route
/// stays as the no-dependency fallback.
/// </summary>
public static class SourceFactory
{
    /// <summary>
    /// What this build can do, best first.
    ///
    /// SimConnect is the right answer since MSFS SU12 made LVARs readable
    /// directly: no extra process, no licence, values pushed at frame rate.
    /// The others are fallbacks for builds without the SimConnect assemblies.
    /// </summary>
    public static string PreferredName => "SimConnect";

    public static bool ClientAvailable =>
#if FSUIPC_CLIENT
        true;
#else
        false;
#endif

    /// <summary>
    /// SimConnect unless asked otherwise. It is always compiled in - there is
    /// no build-time dependency, only a native DLL that must be present at
    /// runtime, and the source says so plainly if it is missing.
    /// </summary>
    public static IDataSource CreateLive(string bridgeDir, bool forceFsuipc = false)
    {
        if (forceFsuipc) return new FsuipcLuaSource(bridgeDir);
        return new SimConnectSource();
    }

    [Obsolete("Use CreateLive")]
    public static IDataSource CreateFsuipc(string bridgeDir) => CreateLive(bridgeDir);
}
