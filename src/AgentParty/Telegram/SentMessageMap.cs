using System.Collections.Concurrent;

namespace AgentParty.Telegram;

internal sealed class SentMessageMap
{
    private readonly int _perClientMaxSize;
    private readonly ConcurrentDictionary<string, ClientBuffer> _byClient = new();

    public SentMessageMap(int perClientMaxSize)
    {
        _perClientMaxSize = perClientMaxSize;
    }

    public void Set(string clientId, int telegramMsgId, string agentPartyMsgId)
    {
        var buffer = _byClient.GetOrAdd(clientId, _ => new ClientBuffer(_perClientMaxSize));
        buffer.Set(telegramMsgId, agentPartyMsgId);
    }

    public bool TryGet(string clientId, int telegramMsgId, out string agentPartyMsgId)
    {
        if (_byClient.TryGetValue(clientId, out var buffer))
            return buffer.TryGet(telegramMsgId, out agentPartyMsgId);

        agentPartyMsgId = default!;
        return false;
    }

    public void Clear(string clientId)
    {
        _byClient.TryRemove(clientId, out _);
    }

    private sealed class ClientBuffer
    {
        private readonly ConcurrentDictionary<int, string> _map = new();
        private readonly ConcurrentQueue<int> _order = new();
        private readonly int _maxSize;

        public ClientBuffer(int maxSize)
        {
            _maxSize = maxSize;
        }

        public void Set(int telegramMsgId, string agentPartyMsgId)
        {
            _map[telegramMsgId] = agentPartyMsgId;
            _order.Enqueue(telegramMsgId);

            while (_map.Count > _maxSize && _order.TryDequeue(out var old))
                _map.TryRemove(old, out _);
        }

        public bool TryGet(int telegramMsgId, out string agentPartyMsgId)
            => _map.TryGetValue(telegramMsgId, out agentPartyMsgId!);
    }
}
