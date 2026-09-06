namespace SimDeck.Core;

public sealed class ModuleInfo
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string ModuleType { get; set; }
    public required string Firmware { get; set; }
    public required string Ip { get; set; }
    public required List<string> Subscriptions { get; set; }
    public double Rate { get; set; }

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public ushort Sequence;
    internal long NextTxTicks;

    /// <summary>Latest resolved values, slot order, for the UI.</summary>
    public float[] Values { get; set; } = Array.Empty<float>();

    public bool Online => DateTime.UtcNow - LastSeen < TimeSpan.FromSeconds(8);
}
