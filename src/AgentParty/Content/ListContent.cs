using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty.Content;

public class ListContent
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("items")]
    public ListItem[] Items { get; set; } = [];

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ListContent Parse(string json) =>
        JsonSerializer.Deserialize<ListContent>(json) ?? throw new JsonException("Invalid ListContent JSON.");
}

public class ListItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("actions")]
    public ListAction[]? Actions { get; set; }
}

public class ListAction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
