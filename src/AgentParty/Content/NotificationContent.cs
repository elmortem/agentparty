using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty.Content;

public class NotificationContent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    public string Serialize() => JsonSerializer.Serialize(this);

    public static NotificationContent Parse(string json) =>
        JsonSerializer.Deserialize<NotificationContent>(json) ?? throw new JsonException("Invalid NotificationContent JSON.");
}
