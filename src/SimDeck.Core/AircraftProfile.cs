using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimDeck.Core;

public sealed class VarSpec
{
    [JsonPropertyName("name")]   public string Name { get; set; } = "";
    [JsonPropertyName("scale")]  public double Scale { get; set; } = 1.0;
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("clamp")]  public double?[]? Clamp { get; set; }
}

public sealed class AircraftProfile
{
    [JsonPropertyName("name")]     public string Name { get; set; } = "";
    [JsonPropertyName("match")]    public List<string> Match { get; set; } = new();
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("vars")]     public Dictionary<string, VarSpec> Vars { get; set; } = new();
    [JsonPropertyName("inputs")]   public Dictionary<string, VarSpec> Inputs { get; set; } = new();

    [JsonIgnore] public string SourcePath { get; set; } = "";

    /// <summary>
    /// Fit score. 0 means no match; higher is more specific.
    ///
    /// The longest matching token wins, so a profile matching "fenix a320"
    /// beats one matching just "a320". Without this a generic profile
    /// loaded earlier silently steals aircraft from a specific one, and
    /// every value comes back as no-data with nothing to explain why.
    /// </summary>
    public int Score(string? aircraft)
    {
        if (string.IsNullOrWhiteSpace(aircraft)) return 0;
        var low = aircraft.ToLowerInvariant();
        int best = 0;
        foreach (var m in Match)
        {
            var t = m.ToLowerInvariant();
            if (low.Contains(t) && t.Length > best) best = t.Length;
        }
        return best == 0 ? 0 : best + Priority;
    }

    public IEnumerable<string> RawNames(IEnumerable<string> logical)
    {
        foreach (var l in logical)
            if (Vars.TryGetValue(l, out var spec))
                yield return spec.Name;
    }

    public float Resolve(string logical, IReadOnlyDictionary<string, double> snapshot)
    {
        if (!Vars.TryGetValue(logical, out var spec)) return float.NaN;
        if (!snapshot.TryGetValue(spec.Name, out var raw)) return float.NaN;

        var v = raw * spec.Scale + spec.Offset;
        if (spec.Clamp is { Length: 2 })
        {
            if (spec.Clamp[0] is { } lo) v = Math.Max(lo, v);
            if (spec.Clamp[1] is { } hi) v = Math.Min(hi, v);
        }
        return (float)v;
    }
}

public sealed class ProfileStore
{
    private readonly string _dir;
    public ProfileStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

    public List<AircraftProfile> Load()
    {
        var list = new List<AircraftProfile>();
        foreach (var path in Directory.GetFiles(_dir, "*.json").OrderBy(p => p))
        {
            try
            {
                var p = JsonSerializer.Deserialize<AircraftProfile>(
                    File.ReadAllText(path), Json.Options);
                if (p is null) continue;
                p.SourcePath = path;
                if (string.IsNullOrEmpty(p.Name)) p.Name = Path.GetFileName(path);
                list.Add(p);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[profiles] bad file {path}: {ex.Message}");
            }
        }
        return list;
    }

    public AircraftProfile? Best(IEnumerable<AircraftProfile> profiles, string? aircraft)
    {
        AircraftProfile? best = null;
        int bestScore = 0;
        foreach (var p in profiles)
        {
            var s = p.Score(aircraft);
            if (s > bestScore) { bestScore = s; best = p; }
        }
        return best;
    }
}
