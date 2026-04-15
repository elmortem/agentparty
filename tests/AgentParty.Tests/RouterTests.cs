namespace AgentParty.Tests;

public class RouterTests
{
    private class FakeServer : IServer
    {
        public event Action<IMessage>? MessageReceived;
        public List<(string ClientId, IMessage Message)> Sent { get; } = new();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) { StartCount++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Task.CompletedTask; }
        public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add((clientId, message));
            return Task.CompletedTask;
        }
        public void SimulateMessage(IMessage message) => MessageReceived?.Invoke(message);
        public void Dispose() => IsDisposed = true;
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
}
