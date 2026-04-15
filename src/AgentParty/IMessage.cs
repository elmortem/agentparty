namespace AgentParty;

public interface IMessage
{
    string Id { get; }
    string Type { get; }
    string Content { get; }
    string ClientId { get; }
    DateTime Timestamp { get; }
}
