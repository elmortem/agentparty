using System.Text.Json;
using AgentParty.Content;

namespace AgentParty.Console;

public class ConsoleServer : IServer
{
    private readonly ConsoleServerConfig _config;
    private readonly ConsoleRenderer _renderer;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private CancellationTokenSource? _cts;
    private Task? _readingTask;
    private bool _isRunning;
    private bool _disposed;
    private bool _startupCommandsSent;

    public event Action<IMessage>? MessageReceived;
    public HashSet<string> AllowedCommands => _config.AllowedCommands;

    public ConsoleServer(ConsoleServerConfig config, ConsoleRenderer? renderer = null,
        TextReader? input = null, TextWriter? output = null)
    {
        _config = config;
        _renderer = renderer ?? new ConsoleRenderer();
        _input = input ?? System.Console.In;
        _output = output ?? System.Console.Out;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!_startupCommandsSent && _config.StartupCommands.Length > 0)
        {
            _startupCommandsSent = true;
            foreach (var cmd in _config.StartupCommands)
            {
                var message = CreateCommandMessage(cmd);
                if (message != null)
                    MessageReceived?.Invoke(message);
            }
        }

        _readingTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);

        _isRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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

    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("ConsoleServer is not running.");

        var rendered = _renderer.Render(message);
        if (!string.IsNullOrEmpty(rendered))
            _output.WriteLine(rendered);

        return Task.CompletedTask;
    }

    private void ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var line = _input.ReadLine();
                if (line == null) break; // EOF

                if (string.IsNullOrWhiteSpace(line)) continue;

                IMessage message;
                if (line.StartsWith('/'))
                {
                    message = CreateCommandMessage(line) ?? CreateTextMessage(line);
                }
                else
                {
                    message = CreateTextMessage(line);
                }

                MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Message CreateTextMessage(string text)
    {
        return new Message
        {
            Type = MessageTypes.Message,
            Content = text,
            ClientId = _config.ClientId
        };
    }

    private Message? CreateCommandMessage(string input)
    {
        var trimmed = input.TrimStart('/');
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];
        var args = parts.Length > 1 ? parts[1..] : null;

        var content = new CommandContent { Name = name, Args = args };

        return new Message
        {
            Type = MessageTypes.Command,
            Content = content.Serialize(),
            ClientId = _config.ClientId
        };
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isRunning)
            StopAsync().GetAwaiter().GetResult();

        _disposed = true;
    }
}
