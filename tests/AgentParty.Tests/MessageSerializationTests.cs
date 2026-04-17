using System.Text.Json;

namespace AgentParty.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void Message_Timestamp_RoundtripsAsUnixSeconds()
    {
        var msg = new Message { Timestamp = new DateTime(2026, 4, 17, 9, 42, 13, 456, DateTimeKind.Utc) };
        var json = JsonSerializer.Serialize(msg);
        Assert.Contains("\"timestamp\":1776418933", json);
        var msg2 = JsonSerializer.Deserialize<Message>(json)!;
        Assert.Equal(new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc), msg2.Timestamp);
        Assert.Equal(DateTimeKind.Utc, msg2.Timestamp.Kind);
    }

    [Fact]
    public void FeedMessage_Timestamp_RoundtripsAsUnixSeconds()
    {
        var msg = new FeedMessage { Timestamp = new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc) };
        var json = JsonSerializer.Serialize(msg);
        Assert.Contains("\"timestamp\":1776418933", json);
        var msg2 = JsonSerializer.Deserialize<FeedMessage>(json)!;
        Assert.Equal(new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc), msg2.Timestamp);
        Assert.Equal(DateTimeKind.Utc, msg2.Timestamp.Kind);
    }
}
