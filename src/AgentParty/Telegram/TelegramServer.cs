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
    private readonly IRawLogger? _rawLogger;
    private readonly SentMessageMap _sentMessages;
    private readonly TelegramRateLimiter _limiter;
    private ITelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isRunning;
    private bool _disposed;

    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;

    public HashSet<string> AllowedCommands => _config.AllowedCommands;

    internal SentMessageMap SentMessages => _sentMessages;

    public TelegramServer(TelegramServerConfig config, TelegramRenderer? renderer = null, IRawLogger? rawLogger = null)
    {
        _config = config;
        _renderer = renderer ?? new TelegramRenderer(config);
        _rawLogger = rawLogger;
        _sentMessages = new SentMessageMap(config.SentMessagesPerClientMaxSize);
        _limiter = new TelegramRateLimiter(config.RateLimit, rawLogger);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        _botClient = CreateBotClient(_config.BotToken);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var options = new ReceiverOptions
        {
            AllowedUpdates =
            [
                UpdateType.Message,
                UpdateType.CallbackQuery,
                UpdateType.ChannelPost,
                UpdateType.EditedMessage,
                UpdateType.EditedChannelPost
            ]
        };
        _pollingTask = RunPollingAsync(_botClient, options, _cts.Token);

        _isRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning) return;

        _cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _pollingTask = null;
        _botClient = null;
        _isRunning = false;
    }

    public async Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isRunning || _botClient == null)
            throw new InvalidOperationException("TelegramServer is not running.");

        var chatId = long.Parse(clientId);
        await _renderer.RenderAsync(_botClient, _limiter, chatId, message,
            trackSentMessage: (telegramMsgId, agentPartyMsgId) => _sentMessages.Set(clientId, telegramMsgId, agentPartyMsgId),
            cancellationToken: cancellationToken);
    }

    public void ClearSentMessagesForClient(string clientId)
    {
        _sentMessages.Clear(clientId);
    }

    protected virtual ITelegramBotClient CreateBotClient(string token)
        => new TelegramBotClient(token);

    protected virtual Task RunPollingAsync(ITelegramBotClient botClient, ReceiverOptions options, CancellationToken cancellationToken)
        => botClient.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, options, cancellationToken);

    internal Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        _rawLogger?.Log("TelegramServer.Update", JsonSerializer.Serialize(update));

        // 1. Callback query
        if (update.CallbackQuery is { } callbackQuery)
        {
            HandleCallbackQuery(callbackQuery);
            return Task.CompletedTask;
        }

        // 2. Determine message
        var msg = update.ChannelPost ?? update.Message;
        if (msg == null) return Task.CompletedTask;

        // 3. Route by chat type
        if (msg.Chat.Type == ChatType.Private)
        {
            var userId = msg.From?.Id;
            if (_config.AllowedUserIds.Count > 0 && (!userId.HasValue || !_config.AllowedUserIds.Contains(userId.Value)))
                return Task.CompletedTask;

            if (msg.Text is { } text)
            {
                var message = new Message
                {
                    Type = MessageTypes.Message,
                    Content = text,
                    ClientId = msg.Chat.Id.ToString(),
                    Timestamp = msg.Date
                };
                MessageReceived?.Invoke(message);
            }
            return Task.CompletedTask;
        }

        // 4. Non-private chat → FeedSources / FeedDiscoveryMode → FeedReceived
        var feedSource = new FeedSource(msg.Chat.Id, msg.MessageThreadId);
        if (_config.FeedDiscoveryMode || _config.FeedSources.Contains(feedSource))
        {
            HandleFeedUpdate(msg, feedSource);
        }

        return Task.CompletedTask;
    }

    internal void HandleFeedUpdate(global::Telegram.Bot.Types.Message msg, FeedSource feedSource)
    {
        var content = msg.Text ?? msg.Caption ?? msg.Document?.FileName;
        if (content == null) return;

        var author = msg.From?.Username ?? msg.From?.FirstName;

        FeedReceived?.Invoke(new FeedMessage
        {
            Content = content,
            Author = author,
            Timestamp = msg.Date,
            Source = feedSource.ToString()
        });
    }

    internal void HandleCallbackQuery(CallbackQuery callbackQuery)
    {
        if (callbackQuery.Message is null || callbackQuery.Data is null)
            return;

        var chatId = callbackQuery.Message.Chat.Id;
        var telegramMsgId = callbackQuery.Message.MessageId;
        var clientId = chatId.ToString();

        if (!_sentMessages.TryGet(clientId, telegramMsgId, out var originalMsgId))
            return; // silent ignore

        ResponseContent responseContent;

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
            ClientId = clientId
        };

        MessageReceived?.Invoke(message);
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isRunning)
            StopAsync().GetAwaiter().GetResult();

        _limiter.Dispose();
        _disposed = true;
    }
}
