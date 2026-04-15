namespace AgentParty;

public interface IClient : IDisposable
{
    event Action<IMessage> MessageReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(IMessage message, CancellationToken cancellationToken = default);
}
