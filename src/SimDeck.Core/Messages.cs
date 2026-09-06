using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimDeck.Core;

/// <summary>Control-plane messages. Field names are the short forms on the
/// wire; keep them short, an ESP32 builds these by hand.</summary>
public sealed class HelloMessage
{
    [JsonPropertyName("t")]    public string Type { get; set; } = "hello";
    [JsonPropertyName("id")]   public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string? ModuleType { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("fw")]   public string? Firmware { get; set; }
    [JsonPropertyName("rate")] public double Rate { get; set; } = 25;
    [JsonPropertyName("sub")]  public List<string> Subscriptions { get; set; } = new();
}

public sealed class EnvelopeMessage
{
    [JsonPropertyName("t")]     public string? Type { get; set; }
    [JsonPropertyName("id")]    public string? Id { get; set; }
    [JsonPropertyName("in")]    public string? Input { get; set; }
    [JsonPropertyName("v")]     public double Value { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("pct")]   public int Percent { get; set; }
    [JsonPropertyName("err")]   public string? Error { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
