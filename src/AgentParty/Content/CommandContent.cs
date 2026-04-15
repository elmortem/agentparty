using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty.Content;

public class CommandContent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static CommandContent Parse(string json) =>
        JsonSerializer.Deserialize<CommandContent>(json) ?? throw new JsonException("Invalid CommandContent JSON.");
}
