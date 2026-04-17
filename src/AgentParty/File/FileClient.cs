using System.Text.Json;

namespace AgentParty.File;

public class FileClient : IClient
{
    private readonly FileClientConfig _config;
    private readonly string _incomingDir;
    private readonly string _myOutgoingDir;
    private readonly string _feedDir;
    private readonly SemaphoreSlim _myOutgoingLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;

    public FileClient(FileClientConfig config)
    {
        _config = config;
        _incomingDir = Path.Combine(config.Directory, "incoming");
        _myOutgoingDir = Path.Combine(config.Directory, "outgoing", config.ClientId);
        _feedDir = Path.Combine(config.Directory, "feed");
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        System.IO.Directory.CreateDirectory(_incomingDir);
        System.IO.Directory.CreateDirectory(_myOutgoingDir);
        System.IO.Directory.CreateDirectory(_feedDir);

        FileServer.CleanupTempFiles(_incomingDir, recursive: false);
        FileServer.CleanupTempFiles(_feedDir, recursive: false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _watcher = new FileSystemWatcher(_myOutgoingDir, "*.json")
        {
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, _) =>
        {
            var cts = _cts;
            if (cts != null)
                _ = ProcessOutgoingFilesAsync(cts.Token);
        };

        _pollingTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { await ProcessOutgoingFilesAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }

                try { await Task.Delay(_config.PollingIntervalMs, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, _cts.Token);

        _isRunning = true;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
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

    public async Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("FileClient is not connected.");

        var envelope = new Message
        {
            Id = message.Id,
            Type = message.Type,
            Content = message.Content,
            ClientId = _config.ClientId,
            Timestamp = message.Timestamp
        };

        var json = JsonSerializer.Serialize(envelope);
        var guid = Guid.NewGuid().ToString();
        var tmpPath = Path.Combine(_incomingDir, $"{guid}.tmp");
        var jsonPath = Path.Combine(_incomingDir, $"{guid}.json");

        await System.IO.File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.Move(tmpPath, jsonPath);
    }

    public async Task SendFeedAsync(IFeedMessage feedMessage, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("FileClient is not connected.");

        var envelope = new FeedMessage
        {
            Content = feedMessage.Content,
            Author = feedMessage.Author,
            Timestamp = feedMessage.Timestamp,
            Source = feedMessage.Source
        };

        var json = JsonSerializer.Serialize(envelope);
        var guid = Guid.NewGuid().ToString();
        var tmpPath = Path.Combine(_feedDir, $"{guid}.tmp");
        var jsonPath = Path.Combine(_feedDir, $"{guid}.json");

        await System.IO.File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.Move(tmpPath, jsonPath);
    }

    private async Task ProcessOutgoingFilesAsync(CancellationToken ct)
    {
        if (_disposed || !_isRunning) return;
        if (!await _myOutgoingLock.WaitAsync(0, ct)) return;
        try
        {
            var files = new DirectoryInfo(_myOutgoingDir)
                .GetFiles("*.json")
                .OrderBy(f => f.CreationTimeUtc)
                .ToArray();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                string content;
                try { content = await System.IO.File.ReadAllTextAsync(file.FullName, ct); }
                catch (FileNotFoundException) { continue; }
                catch (IOException) { continue; }

                try
                {
                    var message = JsonSerializer.Deserialize<Message>(content);
                    if (message == null) continue;

                    if (message.ClientId != _config.ClientId)
                        continue; // sanity: should not happen if server is correct

                    System.IO.File.Delete(file.FullName);
                    MessageReceived?.Invoke(message);
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        finally
        {
            _myOutgoingLock.Release();
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
