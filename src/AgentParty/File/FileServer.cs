using System.Text.Json;

namespace AgentParty.File;

public class FileServer : IServer
{
    private readonly FileServerConfig _config;
    private readonly string _incomingDir;
    private readonly string _outgoingDir;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;

    public FileServer(FileServerConfig config)
    {
        _config = config;
        _incomingDir = Path.Combine(config.Directory, "incoming");
        _outgoingDir = Path.Combine(config.Directory, "outgoing");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        System.IO.Directory.CreateDirectory(_incomingDir);
        System.IO.Directory.CreateDirectory(_outgoingDir);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _watcher = new FileSystemWatcher(_incomingDir, "*.json")
        {
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, _) => ProcessIncomingFiles();

        _pollingTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                ProcessIncomingFiles();
                try { await Task.Delay(_config.PollingIntervalMs, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, _cts.Token);

        _isRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return;

        _isRunning = false;

        _watcher?.Dispose();
        _watcher = null;

        _cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("FileServer is not running.");

        var envelope = new Message
        {
            Id = message.Id,
            Type = message.Type,
            Content = message.Content,
            ClientId = clientId,
            Timestamp = message.Timestamp
        };

        var json = JsonSerializer.Serialize(envelope);
        var guid = Guid.NewGuid().ToString();
        var tmpPath = Path.Combine(_outgoingDir, $"{guid}.tmp");
        var jsonPath = Path.Combine(_outgoingDir, $"{guid}.json");

        System.IO.File.WriteAllText(tmpPath, json);
        System.IO.File.Move(tmpPath, jsonPath);

        return Task.CompletedTask;
    }

    private void ProcessIncomingFiles()
    {
        if (_disposed || !_isRunning) return;

        try
        {
            var files = new DirectoryInfo(_incomingDir)
                .GetFiles("*.json")
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file.FullName);
                    System.IO.File.Delete(file.FullName);

                    var message = JsonSerializer.Deserialize<Message>(json);
                    if (message != null)
                        MessageReceived?.Invoke(message);
                }
                catch (IOException)
                {
                    // File may be in use or already deleted — skip, pick up on next poll
                }
                catch (JsonException)
                {
                    // Malformed file — skip
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory removed externally
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isRunning)
            StopAsync().GetAwaiter().GetResult();

        _disposed = true;
    }
}
