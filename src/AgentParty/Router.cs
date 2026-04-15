using System.Text.Json;
using AgentParty.Content;

namespace AgentParty;

public class Router : IServer
{
    private readonly List<IServer> _servers = new();
    private readonly Dictionary<string, IServer> _routingTable = new();
    private readonly Dictionary<IServer, Action<IMessage>> _handlers = new();
    private readonly Dictionary<IServer, Action<IFeedMessage>> _feedHandlers = new();
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;

    public HashSet<string> AllowedCommands
    {
        get
        {
            var combined = new HashSet<string>();
            foreach (var server in _servers)
                combined.UnionWith(server.AllowedCommands);
            return combined;
        }
    }

    public void Register(IServer server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _servers.Add(server);

        Action<IMessage> handler = message =>
        {
            _routingTable[message.ClientId] = server;

            if (message.Type == MessageTypes.Command)
            {
                try
                {
                    var cmd = CommandContent.Parse(message.Content);
                    if (!server.AllowedCommands.Contains(cmd.Name))
                    {
                        // Command not in whitelist — send error to client
                        var errorMsg = new Message
                        {
                            Type = MessageTypes.Text,
                            Content = $"Command '{cmd.Name}' is not allowed on this channel",
                            ClientId = message.ClientId
                        };
                        server.SendAsync(message.ClientId, errorMsg).GetAwaiter().GetResult();
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Malformed command content — drop
                    return;
                }
            }

            MessageReceived?.Invoke(message);
        };
        _handlers[server] = handler;
        server.MessageReceived += handler;

        Action<IFeedMessage> feedHandler = feedMessage => FeedReceived?.Invoke(feedMessage);
        _feedHandlers[server] = feedHandler;
        server.FeedReceived += feedHandler;

        if (_isRunning)
            server.StartAsync().GetAwaiter().GetResult();
    }

    public void Unregister(IServer server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_handlers.TryGetValue(server, out var handler))
        {
            server.MessageReceived -= handler;
            _handlers.Remove(server);
        }

        if (_feedHandlers.TryGetValue(server, out var feedHandler))
        {
            server.FeedReceived -= feedHandler;
            _feedHandlers.Remove(server);
        }

        _servers.Remove(server);

        var keysToRemove = _routingTable
            .Where(kvp => kvp.Value == server)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in keysToRemove)
            _routingTable.Remove(key);

        if (_isRunning)
            server.StopAsync().GetAwaiter().GetResult();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return;

        foreach (var server in _servers)
            await server.StartAsync(cancellationToken);

        _isRunning = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return;

        _isRunning = false;

        foreach (var server in _servers)
            await server.StopAsync(cancellationToken);
    }

    public async Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning)
            throw new InvalidOperationException("Router is not running.");

        if (!_routingTable.TryGetValue(clientId, out var server))
            throw new KeyNotFoundException($"No route found for clientId '{clientId}'.");

        await server.SendAsync(clientId, message, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isRunning)
            StopAsync().GetAwaiter().GetResult();

        _disposed = true;

        foreach (var server in _servers)
            server.Dispose();

        _servers.Clear();
        _handlers.Clear();
        _feedHandlers.Clear();
        _routingTable.Clear();
    }
}
