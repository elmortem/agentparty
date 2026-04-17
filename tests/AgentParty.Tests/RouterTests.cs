using System.Collections.Concurrent;
using System.Text.Json;
using AgentParty.Content;

namespace AgentParty.Tests;

public class RouterTests
{
    private class FakeServer : IServer
    {
        public event Action<IMessage>? MessageReceived;
        public event Action<IFeedMessage>? FeedReceived;
        public List<(string ClientId, IMessage Message)> Sent { get; } = new();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool ThrowOnSend { get; set; }
        public HashSet<string> AllowedCommands { get; set; } = new();

        public Task StartAsync(CancellationToken cancellationToken = default) { StartCount++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Task.CompletedTask; }
        public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend) throw new InvalidOperationException("send failed");
            Sent.Add((clientId, message));
            return Task.CompletedTask;
        }
        public void SimulateMessage(IMessage message) => MessageReceived?.Invoke(message);
        public void SimulateFeed(IFeedMessage feedMessage) => FeedReceived?.Invoke(feedMessage);
        public void Dispose() => IsDisposed = true;
    }

    private class FakeLogger : IRawLogger
    {
        public ConcurrentBag<(string Source, string Data)> Entries { get; } = new();
        public void Log(string source, string rawData) => Entries.Add((source, rawData));
    }

    private static Message MakeMessage(string clientId = "c1") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Content = "hello",
        ClientId = clientId
    };

    [Fact]
    public async Task StartAsync_StartsAllRegisteredServers()
    {
        var s1 = new FakeServer();
        var s2 = new FakeServer();
        var router = new Router();
        router.Register(s1);
        router.Register(s2);

        await router.StartAsync();

        Assert.Equal(1, s1.StartCount);
        Assert.Equal(1, s2.StartCount);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var s1 = new FakeServer();
        var router = new Router();
        router.Register(s1);

        await router.StartAsync();
        await router.StartAsync();

        Assert.Equal(1, s1.StartCount);
    }

    [Fact]
    public async Task StopAsync_StopsAllServers()
    {
        var s1 = new FakeServer();
        var router = new Router();
        router.Register(s1);
        await router.StartAsync();

        await router.StopAsync();

        Assert.Equal(1, s1.StopCount);
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsNoop()
    {
        var s1 = new FakeServer();
        var router = new Router();
        router.Register(s1);

        await router.StopAsync();

        Assert.Equal(0, s1.StopCount);
    }

    [Fact]
    public async Task MessageReceived_IsPropagatedFromRegisteredServer()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        await router.StartAsync();

        IMessage? received = null;
        router.MessageReceived += m => received = m;

        var msg = MakeMessage();
        server.SimulateMessage(msg);

        Assert.NotNull(received);
        Assert.Equal(msg.Content, received.Content);
    }

    [Fact]
    public async Task SendAsync_RoutesToCorrectServer()
    {
        var s1 = new FakeServer();
        var s2 = new FakeServer();
        var router = new Router();
        router.Register(s1);
        router.Register(s2);
        await router.StartAsync();

        // Simulate message from s2 to register route
        s2.SimulateMessage(MakeMessage("client-from-s2"));

        var reply = MakeMessage("client-from-s2");
        await router.SendAsync("client-from-s2", reply);

        Assert.Empty(s1.Sent);
        Assert.Single(s2.Sent);
        Assert.Equal("client-from-s2", s2.Sent[0].ClientId);
    }

    [Fact]
    public async Task SendAsync_UnknownClientId_ThrowsKeyNotFound()
    {
        var router = new Router();
        router.Register(new FakeServer());
        await router.StartAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => router.SendAsync("unknown", MakeMessage()));
    }

    [Fact]
    public async Task SendAsync_WhenNotRunning_ThrowsInvalidOperation()
    {
        var router = new Router();
        router.Register(new FakeServer());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.SendAsync("c1", MakeMessage()));
    }

    [Fact]
    public void Register_WhileRunning_StartsServerImmediately()
    {
        var router = new Router();
        router.StartAsync().GetAwaiter().GetResult();

        var server = new FakeServer();
        router.Register(server);

        Assert.Equal(1, server.StartCount);
    }

    [Fact]
    public async Task Unregister_WhileRunning_StopsServerAndRemovesRoutes()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        await router.StartAsync();

        server.SimulateMessage(MakeMessage("c1"));
        router.Unregister(server);

        Assert.Equal(1, server.StopCount);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => router.SendAsync("c1", MakeMessage()));
    }

    [Fact]
    public void Dispose_StopsAndDisposesAllServers()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        router.Dispose();

        Assert.Equal(1, server.StopCount);
        Assert.True(server.IsDisposed);
    }

    // --- Command filtering tests ---

    [Fact]
    public void Command_AllowedByWhitelist_IsPropagated()
    {
        var server = new FakeServer { AllowedCommands = new() { "status" } };
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IMessage? received = null;
        router.MessageReceived += m => received = m;

        var cmdContent = new CommandContent { Name = "status", Args = ["project1"] };
        var msg = new Message
        {
            Type = MessageTypes.Command,
            Content = cmdContent.Serialize(),
            ClientId = "c1"
        };
        server.SimulateMessage(msg);

        Assert.NotNull(received);
        Assert.Equal(MessageTypes.Command, received.Type);
    }

    [Fact]
    public void Command_NotInWhitelist_IsBlocked()
    {
        var server = new FakeServer { AllowedCommands = new() { "status" } };
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IMessage? received = null;
        router.MessageReceived += m => received = m;

        var cmdContent = new CommandContent { Name = "shutdown" };
        var msg = new Message
        {
            Type = MessageTypes.Command,
            Content = cmdContent.Serialize(),
            ClientId = "c1"
        };
        server.SimulateMessage(msg);

        Assert.Null(received);
        SpinWait.SpinUntil(() => server.Sent.Count > 0, TimeSpan.FromSeconds(1));
        Assert.Single(server.Sent);
        Assert.Equal(MessageTypes.Text, server.Sent[0].Message.Type);
        Assert.Contains("'shutdown'", server.Sent[0].Message.Content);
        Assert.Contains("not allowed", server.Sent[0].Message.Content);
    }

    [Fact]
    public void Command_EmptyWhitelist_BlocksAllCommands()
    {
        var server = new FakeServer(); // AllowedCommands is empty
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IMessage? received = null;
        router.MessageReceived += m => received = m;

        var cmdContent = new CommandContent { Name = "anything" };
        var msg = new Message
        {
            Type = MessageTypes.Command,
            Content = cmdContent.Serialize(),
            ClientId = "c1"
        };
        server.SimulateMessage(msg);

        Assert.Null(received);
        SpinWait.SpinUntil(() => server.Sent.Count > 0, TimeSpan.FromSeconds(1));
        Assert.Single(server.Sent);
    }

    [Fact]
    public void AllowedCommands_AggregatesFromAllServers()
    {
        var s1 = new FakeServer { AllowedCommands = new() { "status", "help" } };
        var s2 = new FakeServer { AllowedCommands = new() { "shutdown", "status" } };
        var router = new Router();
        router.Register(s1);
        router.Register(s2);

        var combined = router.AllowedCommands;

        Assert.Equal(3, combined.Count);
        Assert.Contains("status", combined);
        Assert.Contains("help", combined);
        Assert.Contains("shutdown", combined);
    }

    [Fact]
    public void NonCommandMessage_IsNotFiltered()
    {
        var server = new FakeServer(); // empty AllowedCommands
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IMessage? received = null;
        router.MessageReceived += m => received = m;

        var msg = MakeMessage(); // Type = "message" (default)
        server.SimulateMessage(msg);

        Assert.NotNull(received);
    }

    // --- Feed tests ---

    [Fact]
    public void FeedReceived_IsPropagatedFromRegisteredServer()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IFeedMessage? received = null;
        router.FeedReceived += f => received = f;

        var feed = new FeedMessage { Content = "news from channel", Author = "TestAuthor" };
        server.SimulateFeed(feed);

        Assert.NotNull(received);
        Assert.Equal("news from channel", received.Content);
        Assert.Equal("TestAuthor", received.Author);
    }

    [Fact]
    public void FeedReceived_AggregatesFromMultipleServers()
    {
        var s1 = new FakeServer();
        var s2 = new FakeServer();
        var router = new Router();
        router.Register(s1);
        router.Register(s2);
        router.StartAsync().GetAwaiter().GetResult();

        var feeds = new List<IFeedMessage>();
        router.FeedReceived += f => feeds.Add(f);

        s1.SimulateFeed(new FeedMessage { Content = "from s1" });
        s2.SimulateFeed(new FeedMessage { Content = "from s2" });

        Assert.Equal(2, feeds.Count);
        Assert.Equal("from s1", feeds[0].Content);
        Assert.Equal("from s2", feeds[1].Content);
    }

    [Fact]
    public void Unregister_StopsFeedPropagation()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        var feeds = new List<IFeedMessage>();
        router.FeedReceived += f => feeds.Add(f);

        server.SimulateFeed(new FeedMessage { Content = "before unregister" });
        router.Unregister(server);
        server.SimulateFeed(new FeedMessage { Content = "after unregister" });

        Assert.Single(feeds);
        Assert.Equal("before unregister", feeds[0].Content);
    }

    // --- Thread-safety and resilience tests ---

    [Fact]
    public async Task ConcurrentMessages_FromMultipleServers_DontCorruptRoutingTable()
    {
        var s1 = new FakeServer();
        var s2 = new FakeServer();
        var router = new Router();
        router.Register(s1);
        router.Register(s2);
        await router.StartAsync();

        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
                s1.SimulateMessage(MakeMessage($"s1-client-{i}"));
        });
        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
                s2.SimulateMessage(MakeMessage($"s2-client-{i}"));
        });
        await Task.WhenAll(t1, t2);

        var ex = await Record.ExceptionAsync(() => router.SendAsync("s1-client-0", MakeMessage("s1-client-0")));
        Assert.Null(ex);
    }

    [Fact]
    public void BlockedCommand_DoesNotThrowOnTransportThread()
    {
        var server = new FakeServer { AllowedCommands = new() { "status" } };
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        var msg = new Message
        {
            Type = MessageTypes.Command,
            Content = new CommandContent { Name = "shutdown" }.Serialize(),
            ClientId = "c1"
        };

        var ex = Record.Exception(() => server.SimulateMessage(msg));
        Assert.Null(ex);

        SpinWait.SpinUntil(() => server.Sent.Count > 0, TimeSpan.FromSeconds(1));
        Assert.Single(server.Sent);
        Assert.Equal(MessageTypes.Text, server.Sent[0].Message.Type);
    }

    [Fact]
    public void SubscriberException_DoesNotPropagate()
    {
        var server = new FakeServer();
        var logger = new FakeLogger();
        var router = new Router(logger);
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        router.MessageReceived += _ => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => server.SimulateMessage(MakeMessage()));
        Assert.Null(ex);
        Assert.NotEmpty(logger.Entries);
    }

    [Fact]
    public void SubscriberException_DoesNotBlockOtherSubscribers()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IMessage? secondReceived = null;
        router.MessageReceived += _ => throw new InvalidOperationException("first fails");
        router.MessageReceived += m => secondReceived = m;

        var msg = MakeMessage();
        server.SimulateMessage(msg);

        Assert.NotNull(secondReceived);
        Assert.Equal(msg.Content, secondReceived.Content);
    }

    [Fact]
    public void FeedSubscriberException_DoesNotPropagate()
    {
        var server = new FakeServer();
        var logger = new FakeLogger();
        var router = new Router(logger);
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        router.FeedReceived += _ => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => server.SimulateFeed(new FeedMessage { Content = "test" }));
        Assert.Null(ex);
        Assert.NotEmpty(logger.Entries);
    }

    [Fact]
    public void FeedSubscriberException_DoesNotBlockOtherSubscribers()
    {
        var server = new FakeServer();
        var router = new Router();
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        IFeedMessage? secondReceived = null;
        router.FeedReceived += _ => throw new InvalidOperationException("first fails");
        router.FeedReceived += f => secondReceived = f;

        server.SimulateFeed(new FeedMessage { Content = "test" });

        Assert.NotNull(secondReceived);
        Assert.Equal("test", secondReceived.Content);
    }

    [Fact]
    public void BlockedCommand_SendAsyncFailure_DoesNotCrash()
    {
        var server = new FakeServer { AllowedCommands = new() { "status" }, ThrowOnSend = true };
        var logger = new FakeLogger();
        var router = new Router(logger);
        router.Register(server);
        router.StartAsync().GetAwaiter().GetResult();

        var msg = new Message
        {
            Type = MessageTypes.Command,
            Content = new CommandContent { Name = "shutdown" }.Serialize(),
            ClientId = "c1"
        };

        var ex = Record.Exception(() => server.SimulateMessage(msg));
        Assert.Null(ex);

        SpinWait.SpinUntil(() => !logger.Entries.IsEmpty, TimeSpan.FromSeconds(1));
        Assert.NotEmpty(logger.Entries);
    }
}
