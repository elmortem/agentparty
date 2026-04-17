using AgentParty.Content;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentParty.Telegram;

public class TelegramRenderer
{
    private readonly TelegramServerConfig _config;

    public TelegramRenderer(TelegramServerConfig config)
    {
        _config = config;
    }

    public virtual async Task RenderAsync(ITelegramBotClient botClient, TelegramRateLimiter limiter, long chatId,
        IMessage message, Action<int, string>? trackSentMessage = null, CancellationToken cancellationToken = default)
    {
        switch (message.Type)
        {
            case MessageTypes.Text:
                await limiter.ExecuteAsync(chatId,
                    ct => botClient.SendMessage(chatId, message.Content, parseMode: ParseMode.Markdown, cancellationToken: ct),
                    cancellationToken);
                break;

            case MessageTypes.Choice:
                await RenderChoiceAsync(botClient, limiter, chatId, message, trackSentMessage, cancellationToken);
                break;

            case MessageTypes.List:
                await RenderListAsync(botClient, limiter, chatId, message, trackSentMessage, cancellationToken);
                break;

            case MessageTypes.Notification:
                await RenderNotificationAsync(botClient, limiter, chatId, message, cancellationToken);
                break;

            default:
                if (!string.IsNullOrEmpty(message.Content))
                    await limiter.ExecuteAsync(chatId,
                        ct => botClient.SendMessage(chatId, message.Content, parseMode: ParseMode.Markdown, cancellationToken: ct),
                        cancellationToken);
                break;
        }
    }

    private async Task RenderChoiceAsync(ITelegramBotClient botClient, TelegramRateLimiter limiter,
        long chatId, IMessage message, Action<int, string>? trackSentMessage, CancellationToken cancellationToken)
    {
        var choice = ChoiceContent.Parse(message.Content);
        var buttons = choice.Options
            .Select(opt => new[] { InlineKeyboardButton.WithCallbackData(opt, opt) })
            .ToArray();
        var markup = new InlineKeyboardMarkup(buttons);

        var sent = await limiter.ExecuteAsync(chatId,
            ct => botClient.SendMessage(chatId, choice.Text, parseMode: ParseMode.Markdown,
                replyMarkup: markup, cancellationToken: ct),
            cancellationToken);
        trackSentMessage?.Invoke(sent.MessageId, message.Id);
    }

    private async Task RenderListAsync(ITelegramBotClient botClient, TelegramRateLimiter limiter,
        long chatId, IMessage message, Action<int, string>? trackSentMessage, CancellationToken cancellationToken)
    {
        var list = ListContent.Parse(message.Content);
        var infoItems = list.Items.Where(i => i.Actions is null or { Length: 0 }).ToList();
        var actionItems = list.Items.Where(i => i.Actions is { Length: > 0 }).ToList();

        if (infoItems.Count > 0)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(list.Title))
                lines.Add($"*{list.Title}*");
            foreach (var item in infoItems)
            {
                var line = $"• {item.Text}";
                if (!string.IsNullOrEmpty(item.Details))
                    line += $" — {item.Details}";
                lines.Add(line);
            }
            await limiter.ExecuteAsync(chatId,
                ct => botClient.SendMessage(chatId, string.Join("\n", lines),
                    parseMode: ParseMode.Markdown, cancellationToken: ct),
                cancellationToken);
        }

        foreach (var item in actionItems)
        {
            var text = item.Text;
            if (!string.IsNullOrEmpty(item.Details))
                text += $"\n{item.Details}";

            var buttons = item.Actions!
                .Select(a => new[] { InlineKeyboardButton.WithCallbackData(a.Label, $"{item.Id}:{a.Id}") })
                .ToArray();
            var markup = new InlineKeyboardMarkup(buttons);

            var sent = await limiter.ExecuteAsync(chatId,
                ct => botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown,
                    replyMarkup: markup, cancellationToken: ct),
                cancellationToken);
            trackSentMessage?.Invoke(sent.MessageId, message.Id);
        }
    }

    private async Task RenderNotificationAsync(ITelegramBotClient botClient, TelegramRateLimiter limiter,
        long chatId, IMessage message, CancellationToken cancellationToken)
    {
        var notification = NotificationContent.Parse(message.Content);
        switch (notification.Kind)
        {
            case "thinking":
                await limiter.ExecuteAsync(chatId,
                    ct => botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct),
                    cancellationToken);
                break;
            case "attention":
                if (!string.IsNullOrEmpty(_config.BotName))
                    await limiter.ExecuteAsync(null,
                        ct => botClient.SetMyName(_config.BotName + _config.AttentionMarker, cancellationToken: ct),
                        cancellationToken);
                break;
            case "attention_clear":
                if (!string.IsNullOrEmpty(_config.BotName))
                    await limiter.ExecuteAsync(null,
                        ct => botClient.SetMyName(_config.BotName, cancellationToken: ct),
                        cancellationToken);
                break;
        }
    }
}
