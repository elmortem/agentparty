namespace AgentParty;

public interface IFeedMessage
{
    string Content { get; }
    string? Author { get; }
    DateTime Timestamp { get; }
    string Source { get; }
}
