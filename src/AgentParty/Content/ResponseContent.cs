using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty.Content;

public class ResponseContent
{
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("items")]
    public ResponseItem[]? Items { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ResponseContent Parse(string json) =>
        JsonSerializer.Deserialize<ResponseContent>(json) ?? throw new JsonException("Invalid ResponseContent JSON.");
}

public class ResponseItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}
