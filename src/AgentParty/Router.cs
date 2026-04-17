using System.Collections.Concurrent;
using System.Text.Json;
using AgentParty.Content;

namespace AgentParty;

public class Router : IServer
{
    private readonly object _lock = new();
    private readonly IRawLogger? _rawLogger;
    private readonly List<IServer> _servers = new();
    private readonly ConcurrentDictionary<string, IServer> _routingTable = new();
    private readonly Dictionary<IServer, Action<IMessage>> _handlers = new();
    private readonly Dictionary<IServer, Action<IFeedMessage>> _feedHandlers = new();
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;

    public Router(IRawLogger? rawLogger = null)
    {
        _rawLogger = rawLogger;
    }

    public HashSet<string> AllowedCommands
    {
        get
        {
            IServer[] snapshot;
            lock (_lock) { snapshot = _servers.ToArray(); }
            var combined = new HashSet<string>();
            foreach (var server in snapshot)
                combined.UnionWith(server.AllowedCommands);
            return combined;
        }
    }

    public void Register(IServer server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldStart;
        lock (_lock)
        {
            _servers.Add(server);

            async void Handler(IMessage message)
            {
                try
                {
                    _routingTable[message.ClientId] = server;

                    if (message.Type == MessageTypes.Command)
                    {
                        try
                        {
                            var cmd = CommandContent.Parse(message.Content);
                            if (!server.AllowedCommands.Contains(cmd.Name))
                            {
                                await SendBlockedCommandErrorAsync(server, message, cmd.Name);
                                return;
                            }
                        }
                        catch (JsonException)
                        {
                            return;
                        }
                    }

                    RaiseMessageReceived(message);
                }
                catch (Exception ex)
                {
                    _rawLogger?.Log("Router.Handler", ex.ToString());
                }
            }

            _handlers[server] = Handler;
            server.MessageReceived += Handler;

            Action<IFeedMessage> feedHandler = feedMessage => RaiseFeedReceived(feedMessage);
            _feedHandlers[server] = feedHandler;
            server.FeedReceived += feedHandler;

            shouldStart = _isRunning;
        }

        if (shouldStart)
            server.StartAsync().GetAwaiter().GetResult();
    }

    public void Unregister(IServer server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldStop;
        lock (_lock)
        {
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

            foreach (var key in _routingTable.Keys.ToArray())
            {
                if (_routingTable.TryGetValue(key, out var val) && val == server)
                    _routingTable.TryRemove(key, out _);
            }

            shouldStop = _isRunning;
        }

        if (shouldStop)
            server.StopAsync().GetAwaiter().GetResult();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return;

        IServer[] snapshot;
        lock (_lock) { snapshot = _servers.ToArray(); }

        foreach (var server in snapshot)
            await server.StartAsync(cancellationToken);

        _isRunning = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return;

        _isRunning = false;

        IServer[] snapshot;
        lock (_lock) { snapshot = _servers.ToArray(); }

        foreach (var server in snapshot)
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

        IServer[] snapshot;
        lock (_lock) { snapshot = _servers.ToArray(); }

        foreach (var server in snapshot)
            server.Dispose();

        lock (_lock)
        {
            _servers.Clear();
            _handlers.Clear();
            _feedHandlers.Clear();
        }
        _routingTable.Clear();
    }

    private async Task SendBlockedCommandErrorAsync(IServer server, IMessage message, string commandName)
    {
        try
        {
            var errorMsg = new Message
            {
                Type = MessageTypes.Text,
                Content = $"Command '{commandName}' is not allowed on this channel",
                ClientId = message.ClientId
            };
            await server.SendAsync(message.ClientId, errorMsg);
        }
        catch (Exception ex)
        {
            _rawLogger?.Log("Router.Handler", ex.ToString());
        }
    }

    private void RaiseMessageReceived(IMessage message)
    {
        var handlers = MessageReceived;
        if (handlers == null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<IMessage>>())
        {
            try { handler(message); }
            catch (Exception ex) { _rawLogger?.Log("Router.Subscriber", ex.ToString()); }
        }
    }

    private void RaiseFeedReceived(IFeedMessage feedMessage)
    {
        var handlers = FeedReceived;
        if (handlers == null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<IFeedMessage>>())
        {
            try { handler(feedMessage); }
            catch (Exception ex) { _rawLogger?.Log("Router.Subscriber", ex.ToString()); }
        }
    }
}
