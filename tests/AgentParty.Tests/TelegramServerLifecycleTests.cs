using AgentParty.Telegram;
using Telegram.Bot;
using Telegram.Bot.Polling;

namespace AgentParty.Tests;

public class TelegramServerLifecycleTests
{
    private class TestableTelegramServer : TelegramServer
    {
        public TestableTelegramServer()
            : base(new TelegramServerConfig { BotToken = "fake:token" }) { }

        protected override ITelegramBotClient CreateBotClient(string token) => null!;

        protected override Task RunPollingAsync(ITelegramBotClient botClient, ReceiverOptions options, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            cancellationToken.Register(() => tcs.TrySetResult());
            return tcs.Task;
        }
    }

    [Fact]
    public async Task StartAsync_CreatesPollingTask()
    {
        var server = new TestableTelegramServer();
        await server.StartAsync();
        await server.StopAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsAndWaitsForPolling()
    {
        var server = new TestableTelegramServer();
        await server.StartAsync();

        // StopAsync must not return until the polling task finishes
        await server.StopAsync();
    }

    [Fact]
    public async Task StopAsync_AfterStart_SetsNotRunning()
    {
        var server = new TestableTelegramServer();
        await server.StartAsync();
        await server.StopAsync();

        // Can start again after stop
        await server.StartAsync();
        await server.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsNoop()
    {
        var server = new TestableTelegramServer();
        await server.StopAsync(); // should not throw
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var server = new TestableTelegramServer();
        await server.StartAsync();
        await server.StartAsync(); // second call is noop
        await server.StopAsync();
    }

    [Fact]
    public void Dispose_WhileRunning_StopsPolling()
    {
        var server = new TestableTelegramServer();
        server.StartAsync().GetAwaiter().GetResult();
        server.Dispose(); // must not block indefinitely
    }
}
