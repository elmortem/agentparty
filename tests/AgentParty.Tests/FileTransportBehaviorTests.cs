using System.Text.Json;
using AgentParty.File;

namespace AgentParty.Tests;

public class FileTransportBehaviorTests : IDisposable
{
    private readonly string _tempDir;

    public FileTransportBehaviorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentparty-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task Server_CleansUpTmpFilesInOutgoingOnStart()
    {
        var outgoingClientDir = Path.Combine(_tempDir, "outgoing", "client-x");
        Directory.CreateDirectory(outgoingClientDir);
        var staleTmp = Path.Combine(outgoingClientDir, "stale.tmp");
        await System.IO.File.WriteAllTextAsync(staleTmp, "leftover");

        var config = new FileServerConfig { Directory = _tempDir };
        using var server = new FileServer(config);
        await server.StartAsync();
        await server.StopAsync();

        Assert.False(System.IO.File.Exists(staleTmp));
    }

    [Fact]
    public async Task Client_CleansUpTmpFilesInIncomingOnConnect()
    {
        var incomingDir = Path.Combine(_tempDir, "incoming");
        Directory.CreateDirectory(incomingDir);
        var staleTmp = Path.Combine(incomingDir, "stale.tmp");
        await System.IO.File.WriteAllTextAsync(staleTmp, "leftover");

        var config = new FileClientConfig { Directory = _tempDir, ClientId = "client-x" };
        using var client = new FileClient(config);
        await client.ConnectAsync();
        await client.DisconnectAsync();

        Assert.False(System.IO.File.Exists(staleTmp));
    }

    [Fact]
    public async Task Server_ConcurrentProcessCalls_NoDuplicateDelivery()
    {
        var config = new FileServerConfig { Directory = _tempDir, PollingIntervalMs = 60_000 };
        using var server = new FileServer(config);
        await server.StartAsync();

        var received = new List<IMessage>();
        server.MessageReceived += m => { lock (received) received.Add(m); };

        var incomingDir = Path.Combine(_tempDir, "incoming");
        var envelope = new Message { Content = "once", ClientId = "c" };
        var json = JsonSerializer.Serialize(envelope);
        var filePath = Path.Combine(incomingDir, $"{Guid.NewGuid()}.json");
        await System.IO.File.WriteAllTextAsync(filePath, json);

        // Two concurrent calls — only one should process the file
        var t1 = server.ProcessIncomingFilesAsync(CancellationToken.None);
        var t2 = server.ProcessIncomingFilesAsync(CancellationToken.None);
        await Task.WhenAll(t1, t2);

        // Give watcher a moment in case it also fired
        await Task.Delay(200);

        await server.StopAsync();

        Assert.Single(received);
    }

    [Fact]
    public async Task Server_WritesToClientSubdirectory()
    {
        var config = new FileServerConfig { Directory = _tempDir };
        using var server = new FileServer(config);
        await server.StartAsync();

        await server.SendAsync("client-a", new Message { Content = "hello" });

        await server.StopAsync();

        var clientDir = Path.Combine(_tempDir, "outgoing", "client-a");
        var files = Directory.GetFiles(clientDir, "*.json");
        Assert.Single(files);

        // Ensure nothing landed directly in outgoing/
        var topFiles = Directory.GetFiles(Path.Combine(_tempDir, "outgoing"), "*.json");
        Assert.Empty(topFiles);
    }

    [Fact]
    public async Task Client_DoesNotSeeOtherClientsSubdir()
    {
        // Place a file manually in outgoing/client-b/ — client-a must not receive it
        var clientBDir = Path.Combine(_tempDir, "outgoing", "client-b");
        Directory.CreateDirectory(clientBDir);
        var envelope = new Message { Content = "not for you", ClientId = "client-b" };
        var json = JsonSerializer.Serialize(envelope);
        await System.IO.File.WriteAllTextAsync(Path.Combine(clientBDir, $"{Guid.NewGuid()}.json"), json);

        var clientConfig = new FileClientConfig { Directory = _tempDir, ClientId = "client-a", PollingIntervalMs = 100 };
        using var client = new FileClient(clientConfig);
        var messages = new List<IMessage>();
        client.MessageReceived += m => { lock (messages) messages.Add(m); };

        await client.ConnectAsync();
        await Task.Delay(500);
        await client.DisconnectAsync();

        Assert.Empty(messages);
    }

    [Fact]
    public async Task SendAsync_Cancelled_DoesNotWriteJsonFile()
    {
        var config = new FileServerConfig { Directory = _tempDir };
        using var server = new FileServer(config);
        await server.StartAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.SendAsync("client-a", new Message { Content = "test" }, cts.Token));

        var outgoing = Path.Combine(_tempDir, "outgoing");
        var jsonFiles = Directory.GetFiles(outgoing, "*.json", SearchOption.AllDirectories);
        Assert.Empty(jsonFiles);

        await server.StopAsync();
    }
}
