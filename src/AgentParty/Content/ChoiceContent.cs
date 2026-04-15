using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty.Content;

public class ChoiceContent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public string[] Options { get; set; } = [];

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ChoiceContent Parse(string json) =>
        JsonSerializer.Deserialize<ChoiceContent>(json) ?? throw new JsonException("Invalid ChoiceContent JSON.");
}
