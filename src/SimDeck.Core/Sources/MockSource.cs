namespace SimDeck.Core.Sources;

/// <summary>
/// Lets you build and debug hardware with the sim closed. Simulates the A320
/// brake system loosely enough to exercise every needle.
/// </summary>
public sealed class MockSource : IDataSource
{
    private readonly double _cycle;
    private readonly DateTime _t0 = DateTime.UtcNow;
    private bool _running;

    public MockSource(double cycleSeconds = 20.0) => _cycle = cycleSeconds;

    public string DisplayName => "Mock";

    public SourceStatus Status => new(
        "mock source · simulated data",
        "This is bench mode. Restart without --mock for live sim data.");
    public bool Connected => _running;

    // Deliberately contains no real aircraft name. An earlier version called
    // itself "Fenix A320 (MOCK)", matched the Fenix profile, and every value
    // silently resolved against the wrong variable names.
    public string? Aircraft => "SimDeck Mock Bench";

    public void Start() => _running = true;
    public void SetWatchlist(IReadOnlyCollection<string> names) { }
    public void Dispose() => _running = false;

    public IReadOnlyDictionary<string, double> Read()
    {
        var t = (DateTime.UtcNow - _t0).TotalSeconds;
        var phase = (t % _cycle) / _cycle;

        var accum = 2950.0 + 60.0 * Math.Sin(t * 0.8);
        var target = phase < 0.40 ? 2700.0 : 0.0;
        var ramp = Math.Min(1.0, (phase % 0.40) / 0.05);

        var left = Math.Max(0.0, target * ramp + 40.0 * Math.Sin(t * 2.1));
        var right = Math.Max(0.0, target * ramp + 40.0 * Math.Sin(t * 2.1 + 0.4));

        return new Dictionary<string, double>
        {
            ["MOCK_ACCUM_PSI"]   = accum,
            ["MOCK_BRAKE_L_PSI"] = left,
            ["MOCK_BRAKE_R_PSI"] = right,
        };
    }

    public bool TryWrite(string name, double value)
    {
        Console.WriteLine($"[mock] write {name} = {value}");
        return true;
    }
}
