using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentParty.Telegram;

public class TelegramServer : IServer
{
    private readonly TelegramServerConfig _config;
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;

    public TelegramServer(TelegramServerConfig config)
    {
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        _botClient = new TelegramBotClient(_config.BotToken);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            },
            cancellationToken: _cts.Token
        );

        _isRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        return Task.CompletedTask;
    }

    public async Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning || _botClient == null)
            throw new InvalidOperationException("TelegramServer is not running.");

        var chatId = long.Parse(clientId);
        await _botClient.SendMessage(chatId, message.Content, cancellationToken: cancellationToken);
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message?.Text is not { } text)
            return Task.CompletedTask;

        var chatId = update.Message.Chat.Id;

        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Type = "message",
            Content = text,
            ClientId = chatId.ToString(),
            Timestamp = update.Message.Date
        };

        MessageReceived?.Invoke(message);
        return Task.CompletedTask;
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        // Polling errors are transient — Telegram.Bot will retry automatically
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isRunning)
            StopAsync().GetAwaiter().GetResult();
    }
}
