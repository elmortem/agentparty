using AgentParty.File;

namespace AgentParty.Tests;

public class FileTransportTests : IDisposable
{
    private readonly string _tempDir;

    public FileTransportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentparty-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ClientSendsMessage_ServerReceivesIt()
    {
        var serverConfig = new FileServerConfig { Directory = _tempDir, PollingIntervalMs = 100 };
        var clientConfig = new FileClientConfig { Directory = _tempDir, ClientId = "test-client", PollingIntervalMs = 100 };

        using var server = new FileServer(serverConfig);
        using var client = new FileClient(clientConfig);

        IMessage? received = null;
        var tcs = new TaskCompletionSource<IMessage>();
        server.MessageReceived += m => tcs.TrySetResult(m);

        await server.StartAsync();
        await client.ConnectAsync();

        await client.SendAsync(new Message { Content = "hello from client", ClientId = "test-client" });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);

        received = tcs.Task.Result;
        Assert.Equal("hello from client", received.Content);
        Assert.Equal("test-client", received.ClientId);
    }

    [Fact]
    public async Task ServerSendsMessage_ClientReceivesIt()
    {
        var serverConfig = new FileServerConfig { Directory = _tempDir, PollingIntervalMs = 100 };
        var clientConfig = new FileClientConfig { Directory = _tempDir, ClientId = "test-client", PollingIntervalMs = 100 };

        using var server = new FileServer(serverConfig);
        using var client = new FileClient(clientConfig);

        var tcs = new TaskCompletionSource<IMessage>();
        client.MessageReceived += m => tcs.TrySetResult(m);

        await server.StartAsync();
        await client.ConnectAsync();

        await server.SendAsync("test-client", new Message { Content = "hello from server" });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, result);

        var received = tcs.Task.Result;
        Assert.Equal("hello from server", received.Content);
        Assert.Equal("test-client", received.ClientId);
    }

    [Fact]
    public async Task Client_IgnoresMessagesForOtherClients()
    {
        var serverConfig = new FileServerConfig { Directory = _tempDir, PollingIntervalMs = 100 };
        var clientConfig = new FileClientConfig { Directory = _tempDir, ClientId = "client-a", PollingIntervalMs = 100 };

        using var server = new FileServer(serverConfig);
        using var client = new FileClient(clientConfig);

        var messages = new List<IMessage>();
        client.MessageReceived += m => messages.Add(m);

        await server.StartAsync();
        await client.ConnectAsync();

        // Send to a different client
        await server.SendAsync("client-b", new Message { Content = "not for you" });
        // Send to our client
        await server.SendAsync("client-a", new Message { Content = "for you" });

        await Task.Delay(1000);

        Assert.Single(messages);
        Assert.Equal("for you", messages[0].Content);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var config = new FileServerConfig { Directory = _tempDir };
        using var server = new FileServer(config);

        await server.StartAsync();
        await server.StartAsync(); // no-op

        await server.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenNotRunning_Throws()
    {
        var config = new FileServerConfig { Directory = _tempDir };
        using var server = new FileServer(config);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SendAsync("c1", new Message()));
    }
}
