using System.Collections.Concurrent;
using System.Text.Json;
using AgentParty.Content;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentParty.Telegram;

public class TelegramServer : IServer
{
    private readonly TelegramServerConfig _config;
    private readonly TelegramRenderer _renderer;
    private readonly ConcurrentDictionary<int, string> _sentMessageMap = new(); // telegramMsgId → agentPartyMsgId
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;

    public HashSet<string> AllowedCommands => _config.AllowedCommands;

    public TelegramServer(TelegramServerConfig config, TelegramRenderer? renderer = null)
    {
        _config = config;
        _renderer = renderer ?? new TelegramRenderer(config);
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
                AllowedUpdates =
                [
                    UpdateType.Message,
                    UpdateType.CallbackQuery,
                    UpdateType.ChannelPost,
                    UpdateType.EditedMessage,
                    UpdateType.EditedChannelPost
                ]
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
        await _renderer.RenderAsync(_botClient, chatId, message,
            trackSentMessage: (telegramMsgId, agentPartyMsgId) => _sentMessageMap[telegramMsgId] = agentPartyMsgId,
            cancellationToken: cancellationToken);
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // 1. Check if this is a feed chat
        var chatId = update.ChannelPost?.Chat.Id ?? update.Message?.Chat.Id;
        if (chatId.HasValue && _config.FeedChatIds.Contains(chatId.Value))
        {
            HandleFeedUpdate(update);
            return Task.CompletedTask;
        }

        // 2. Callback query
        if (update.CallbackQuery is { } callbackQuery)
        {
            HandleCallbackQuery(callbackQuery);
            return Task.CompletedTask;
        }

        // 3. Text message from allowed user
        if (update.Message?.Text is not { } text)
            return Task.CompletedTask;

        if (_config.AllowedUserId != 0 && update.Message.From?.Id != _config.AllowedUserId)
            return Task.CompletedTask;

        var message = new Message
        {
            Type = MessageTypes.Message,
            Content = text,
            ClientId = update.Message.Chat.Id.ToString(),
            Timestamp = update.Message.Date
        };

        MessageReceived?.Invoke(message);
        return Task.CompletedTask;
    }

    private void HandleFeedUpdate(Update update)
    {
        var msg = update.ChannelPost ?? update.Message;
        if (msg == null) return;

        var content = msg.Text ?? msg.Caption ?? msg.Document?.FileName;
        if (content == null) return;

        var author = msg.From?.Username ?? msg.From?.FirstName;

        FeedReceived?.Invoke(new FeedMessage
        {
            Content = content,
            Author = author,
            Timestamp = msg.Date
        });
    }

    private void HandleCallbackQuery(CallbackQuery callbackQuery)
    {
        if (callbackQuery.Message is null || callbackQuery.Data is null)
            return;

        var chatId = callbackQuery.Message.Chat.Id;
        var telegramMsgId = callbackQuery.Message.MessageId;

        // Find the original AgentParty message ID
        _sentMessageMap.TryGetValue(telegramMsgId, out var originalMsgId);
        originalMsgId ??= telegramMsgId.ToString();

        ResponseContent responseContent;

        // Check if it's a list item action (format: "itemId:actionId")
        if (callbackQuery.Data.Contains(':'))
        {
            var parts = callbackQuery.Data.Split(':', 2);
            responseContent = new ResponseContent
            {
                To = originalMsgId,
                Items = [new ResponseItem { Id = parts[0], Action = parts[1] }]
            };
        }
        else
        {
            responseContent = new ResponseContent
            {
                To = originalMsgId,
                Value = callbackQuery.Data
            };
        }

        var message = new Message
        {
            Type = MessageTypes.Response,
            Content = responseContent.Serialize(),
            ClientId = chatId.ToString()
        };

        MessageReceived?.Invoke(message);
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
