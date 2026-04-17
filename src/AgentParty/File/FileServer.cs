using System.Text.Json;

namespace AgentParty.File;

public class FileServer : IServer
{
    private readonly FileServerConfig _config;
    private readonly IRawLogger? _rawLogger;
    private readonly string _incomingDir;
    private readonly string _outgoingDir;
    private readonly string _feedDir;
    private readonly SemaphoreSlim _incomingLock = new(1, 1);
    private readonly SemaphoreSlim _feedLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _feedWatcher;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;
    public HashSet<string> AllowedCommands => _config.AllowedCommands;

    public FileServer(FileServerConfig config, IRawLogger? rawLogger = null)
    {
        _config = config;
        _rawLogger = rawLogger;
        _incomingDir = Path.Combine(config.Directory, "incoming");
        _outgoingDir = Path.Combine(config.Directory, "outgoing");
        _feedDir = Path.Combine(config.Directory, "feed");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        System.IO.Directory.CreateDirectory(_incomingDir);
        System.IO.Directory.CreateDirectory(_outgoingDir);
        System.IO.Directory.CreateDirectory(_feedDir);

        CleanupTempFiles(_outgoingDir, recursive: true);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _watcher = new FileSystemWatcher(_incomingDir, "*.json")
        {
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, _) =>
        {
            var cts = _cts;
            if (cts != null)
                _ = ProcessIncomingFilesAsync(cts.Token);
        };

        _feedWatcher = new FileSystemWatcher(_feedDir, "*.json")
        {
            EnableRaisingEvents = true
        };
        _feedWatcher.Created += (_, _) =>
        {
            var cts = _cts;
            if (cts != null)
                _ = ProcessFeedFilesAsync(cts.Token);
        };

        _pollingTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await ProcessIncomingFilesAsync(_cts.Token);
                    await ProcessFeedFilesAsync(_cts.Token);
                }
                catch (OperationCanceledException) { break; }

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

        _feedWatcher?.Dispose();
        _feedWatcher = null;

        _cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public async Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
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
        var clientDir = Path.Combine(_outgoingDir, clientId);
        System.IO.Directory.CreateDirectory(clientDir);
        var tmpPath = Path.Combine(clientDir, $"{guid}.tmp");
        var jsonPath = Path.Combine(clientDir, $"{guid}.json");

        await System.IO.File.WriteAllTextAsync(tmpPath, json, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.Move(tmpPath, jsonPath);
    }

    internal async Task ProcessIncomingFilesAsync(CancellationToken ct)
    {
        if (_disposed || !_isRunning) return;
        if (!await _incomingLock.WaitAsync(0, ct)) return;
        try
        {
            var files = new DirectoryInfo(_incomingDir)
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

                try { System.IO.File.Delete(file.FullName); }
                catch (FileNotFoundException) { }
                catch (IOException) { continue; }

                _rawLogger?.Log("FileServer.Incoming", content);

                try
                {
                    var message = JsonSerializer.Deserialize<Message>(content);
                    if (message != null)
                        MessageReceived?.Invoke(message);
                }
                catch (JsonException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        finally
        {
            _incomingLock.Release();
        }
    }

    internal async Task ProcessFeedFilesAsync(CancellationToken ct)
    {
        if (_disposed || !_isRunning) return;
        if (!await _feedLock.WaitAsync(0, ct)) return;
        try
        {
            var files = new DirectoryInfo(_feedDir)
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

                _rawLogger?.Log("FileServer.Feed", content);

                try
                {
                    var feedMessage = JsonSerializer.Deserialize<FeedMessage>(content);
                    if (feedMessage != null)
                    {
                        System.IO.File.Delete(file.FullName);
                        FeedReceived?.Invoke(feedMessage);
                    }
                }
                catch (JsonException)
                {
                    System.Console.Error.WriteLine($"[FileServer] Bad feed file: {file.Name}");
                }
                catch (IOException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        finally
        {
            _feedLock.Release();
        }
    }

    internal static void CleanupTempFiles(string dir, bool recursive)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var tmp in System.IO.Directory.GetFiles(dir, "*.tmp", option))
        {
            try { System.IO.File.Delete(tmp); } catch (IOException) { }
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
