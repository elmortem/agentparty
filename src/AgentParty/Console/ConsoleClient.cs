using System.Text.Json;

namespace AgentParty.Console;

public class ConsoleClient : IClient
{
    private readonly ConsoleClientConfig _config;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private CancellationTokenSource? _cts;
    private Task? _readingTask;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;

    public ConsoleClient(ConsoleClientConfig config, TextReader? input = null, TextWriter? output = null)
    {
        _config = config;
        _input = input ?? System.Console.In;
        _output = output ?? System.Console.Out;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readingTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);

        _isRunning = true;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return;

        _isRunning = false;

        _cts?.Cancel();
        if (_readingTask != null)
        {
            try { await _readingTask; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("ConsoleClient is not connected.");

        var envelope = new Message
        {
            Id = message.Id,
            Type = message.Type,
            Content = message.Content,
            ClientId = _config.ClientId,
            Timestamp = message.Timestamp
        };

        var json = JsonSerializer.Serialize(envelope);
        _output.WriteLine(json);

        return Task.CompletedTask;
    }

    private void ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var line = _input.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var message = JsonSerializer.Deserialize<Message>(line);
                if (message != null)
                    MessageReceived?.Invoke(message);
            }
            catch (JsonException)
            {
                // Malformed input — skip
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isRunning)
            DisconnectAsync().GetAwaiter().GetResult();

        _disposed = true;
    }
}
