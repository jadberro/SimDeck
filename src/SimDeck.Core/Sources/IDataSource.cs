namespace SimDeck.Core.Sources;

/// <summary>
/// Anything that can supply named values. The hub never knows whether it is
/// talking to FSUIPC, a mock, or X-Plane later on.
///
/// Names in and out are RAW source names (an actual LVAR string). Mapping
/// from logical names happens in the hub, via the aircraft profile.
/// </summary>
public interface IDataSource : IDisposable
{
    string DisplayName { get; }

    /// <summary>How this source is doing, in its own words.</summary>
    SourceStatus Status { get; }

    /// <summary>True while the source is actually receiving from the sim.</summary>
    bool Connected { get; }

    /// <summary>Aircraft title, used for profile matching. Null if unknown.</summary>
    string? Aircraft { get; }

    void Start();

    /// <summary>
    /// Which raw names the hub currently cares about. Called whenever the set
    /// of subscribed modules changes. Sources that must poll use this to build
    /// their poll list; sources that get everything free ignore it.
    /// </summary>
    void SetWatchlist(IReadOnlyCollection<string> names);

    /// <summary>Latest snapshot. Missing names are simply absent.</summary>
    IReadOnlyDictionary<string, double> Read();

    /// <summary>Push a value back into the sim, for module inputs.</summary>
    bool TryWrite(string name, double value);
}
