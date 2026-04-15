namespace AgentParty;

public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
