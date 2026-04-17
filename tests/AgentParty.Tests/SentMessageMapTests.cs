using AgentParty.Telegram;

namespace AgentParty.Tests;

public class SentMessageMapTests
{
    [Fact]
    public void Set_TryGet_ReturnsValue()
    {
        var map = new SentMessageMap(10);
        map.Set("c1", 42, "msg-001");

        Assert.True(map.TryGet("c1", 42, out var id));
        Assert.Equal("msg-001", id);
    }

    [Fact]
    public void TryGet_UnknownClient_ReturnsFalse()
    {
        var map = new SentMessageMap(10);

        Assert.False(map.TryGet("unknown", 42, out _));
    }

    [Fact]
    public void TryGet_UnknownMessageId_ReturnsFalse()
    {
        var map = new SentMessageMap(10);
        map.Set("c1", 42, "msg-001");

        Assert.False(map.TryGet("c1", 99, out _));
    }

    [Fact]
    public void Set_UpdatesExistingKey()
    {
        var map = new SentMessageMap(10);
        map.Set("c1", 42, "msg-001");
        map.Set("c1", 42, "msg-002");

        Assert.True(map.TryGet("c1", 42, out var id));
        Assert.Equal("msg-002", id);
    }

    [Fact]
    public void Set_ExceedsPerClientMaxSize_EvictsOldest()
    {
        var map = new SentMessageMap(3);
        map.Set("c1", 1, "a");
        map.Set("c1", 2, "b");
        map.Set("c1", 3, "c");
        map.Set("c1", 4, "d"); // evicts id=1

        Assert.False(map.TryGet("c1", 1, out _));
        Assert.True(map.TryGet("c1", 2, out _));
        Assert.True(map.TryGet("c1", 3, out _));
        Assert.True(map.TryGet("c1", 4, out _));
    }

    [Fact]
    public void Set_TwoClients_IndependentBuffers()
    {
        var map = new SentMessageMap(2);
        map.Set("a", 1, "a1");
        map.Set("a", 2, "a2");
        map.Set("b", 1, "b1");
        map.Set("b", 2, "b2");

        map.Set("a", 3, "a3"); // evicts a's id=1

        Assert.False(map.TryGet("a", 1, out _));
        Assert.True(map.TryGet("b", 1, out _)); // b unaffected
        Assert.True(map.TryGet("b", 2, out _));
    }

    [Fact]
    public void Clear_RemovesAllEntriesForClient()
    {
        var map = new SentMessageMap(10);
        map.Set("a", 1, "a1");
        map.Set("a", 2, "a2");
        map.Set("b", 1, "b1");

        map.Clear("a");

        Assert.False(map.TryGet("a", 1, out _));
        Assert.False(map.TryGet("a", 2, out _));
        Assert.True(map.TryGet("b", 1, out _));
    }

    [Fact]
    public void Clear_UnknownClient_DoesNotThrow()
    {
        var map = new SentMessageMap(10);
        var ex = Record.Exception(() => map.Clear("nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void Set_ConcurrentAdds_DoesNotThrow()
    {
        var map = new SentMessageMap(100);
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() =>
            {
                for (int j = 0; j < 1000; j++)
                    map.Set($"client-{i}", j, $"msg-{i}-{j}");
            }))
            .ToArray();
        Task.WaitAll(tasks);
    }
}
