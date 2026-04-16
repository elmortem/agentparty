using System.Text.Json.Serialization;

namespace AgentParty;

public class FeedMessage : IFeedMessage
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
}
