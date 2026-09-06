namespace SimDeck.Core.Ota;

public enum OtaState { Idle, Offered, Running, Done, Failed }

public sealed class OtaStatus
{
    public OtaState State { get; set; } = OtaState.Idle;
    public int Percent { get; set; }
    public string? Version { get; set; }
    public string Message { get; set; } = "";
    public DateTime Changed { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-module OTA bookkeeping, kept outside the module registry so a module
/// dropping off the network mid-flash does not lose its update history. It
/// goes quiet while it writes and reboots; that is expected, not a failure.
/// </summary>
public sealed class OtaTracker
{
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ReofferDelay = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, OtaStatus> _state = new();
    private readonly object _lock = new();

    public OtaStatus Get(string moduleId)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(moduleId, out var s))
                _state[moduleId] = s = new OtaStatus();
            return s;
        }
    }

    public void Set(string moduleId, OtaState state, int? percent = null,
                    string? version = null, string? message = null)
    {
        lock (_lock)
        {
            var s = Get(moduleId);
            s.State = state;
            if (percent is { } p) s.Percent = p;
            if (version is not null) s.Version = version;
            if (message is not null) s.Message = message;
            s.Changed = DateTime.UtcNow;
        }
    }

    public bool ShouldOffer(string moduleId)
    {
        lock (_lock)
        {
            var s = Get(moduleId);
            var age = DateTime.UtcNow - s.Changed;

            if (s.State is OtaState.Offered or OtaState.Running)
            {
                if (age > OfferTimeout)
                {
                    s.State = OtaState.Failed;
                    s.Message = "timed out";
                    s.Changed = DateTime.UtcNow;
                }
                return false;                      // already in flight
            }
            if (s.State == OtaState.Failed && age < ReofferDelay) return false;
            return true;
        }
    }

    /// <summary>Numeric compare, so 1.10.0 beats 1.9.0 and date stamps like
    /// 2026.09.05 work as versions.</summary>
    public static bool IsNewer(string candidate, string current)
        => Compare(candidate, current) > 0;

    public static int Compare(string a, string b)
    {
        var pa = Parse(a); var pb = Parse(b);
        for (int i = 0; i < 3; i++)
        {
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        }
        return 0;
    }

    private static int[] Parse(string? v)
    {
        var outp = new[] { 0, 0, 0 };
        if (string.IsNullOrWhiteSpace(v)) return outp;
        var parts = System.Text.RegularExpressions.Regex.Matches(v, @"\d+");
        for (int i = 0; i < Math.Min(3, parts.Count); i++)
            outp[i] = int.TryParse(parts[i].Value, out var n) ? n : 0;
        return outp;
    }
}
