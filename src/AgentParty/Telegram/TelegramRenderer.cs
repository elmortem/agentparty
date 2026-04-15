using AgentParty.Content;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentParty.Telegram;

public class TelegramRenderer
{
    private readonly TelegramServerConfig _config;

    public TelegramRenderer(TelegramServerConfig config)
    {
        _config = config;
    }

    public virtual async Task RenderAsync(ITelegramBotClient botClient, long chatId, IMessage message,
        Action<int, string>? trackSentMessage = null, CancellationToken cancellationToken = default)
    {
        switch (message.Type)
        {
            case MessageTypes.Text:
                await botClient.SendMessage(chatId, message.Content, cancellationToken: cancellationToken);
                break;

            case MessageTypes.Choice:
                await RenderChoiceAsync(botClient, chatId, message, trackSentMessage, cancellationToken);
                break;

            case MessageTypes.List:
                await RenderListAsync(botClient, chatId, message, trackSentMessage, cancellationToken);
                break;

            case MessageTypes.Notification:
                await RenderNotificationAsync(botClient, chatId, message, cancellationToken);
                break;

            default:
                // Fallback — send content as text
                if (!string.IsNullOrEmpty(message.Content))
                    await botClient.SendMessage(chatId, message.Content, cancellationToken: cancellationToken);
                break;
        }
    }

    protected virtual async Task RenderChoiceAsync(ITelegramBotClient botClient, long chatId, IMessage message,
        Action<int, string>? trackSentMessage, CancellationToken cancellationToken)
    {
        var choice = ChoiceContent.Parse(message.Content);
        var buttons = choice.Options
            .Select(opt => new[] { InlineKeyboardButton.WithCallbackData(opt, opt) })
            .ToArray();
        var markup = new InlineKeyboardMarkup(buttons);

        var sent = await botClient.SendMessage(chatId, choice.Text,
            replyMarkup: markup, cancellationToken: cancellationToken);
        trackSentMessage?.Invoke(sent.MessageId, message.Id);
    }

    protected virtual async Task RenderListAsync(ITelegramBotClient botClient, long chatId, IMessage message,
        Action<int, string>? trackSentMessage, CancellationToken cancellationToken)
    {
        var list = ListContent.Parse(message.Content);
        var infoItems = list.Items.Where(i => i.Actions is null or { Length: 0 }).ToList();
        var actionItems = list.Items.Where(i => i.Actions is { Length: > 0 }).ToList();

        // Info items — single text message
        if (infoItems.Count > 0)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(list.Title))
                lines.Add($"<b>{list.Title}</b>");
            foreach (var item in infoItems)
            {
                var line = $"• {item.Text}";
                if (!string.IsNullOrEmpty(item.Details))
                    line += $" — {item.Details}";
                lines.Add(line);
            }
            await botClient.SendMessage(chatId, string.Join("\n", lines),
                parseMode: global::Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
        }

        // Action items — each as a separate message with inline buttons
        foreach (var item in actionItems)
        {
            var text = item.Text;
            if (!string.IsNullOrEmpty(item.Details))
                text += $"\n{item.Details}";

            var buttons = item.Actions!
                .Select(a => new[] { InlineKeyboardButton.WithCallbackData(a.Label, $"{item.Id}:{a.Id}") })
                .ToArray();
            var markup = new InlineKeyboardMarkup(buttons);

            var sent = await botClient.SendMessage(chatId, text,
                replyMarkup: markup, cancellationToken: cancellationToken);
            trackSentMessage?.Invoke(sent.MessageId, message.Id);
        }
    }

    protected virtual async Task RenderNotificationAsync(ITelegramBotClient botClient, long chatId, IMessage message,
        CancellationToken cancellationToken)
    {
        var notification = NotificationContent.Parse(message.Content);
        switch (notification.Kind)
        {
            case "thinking":
                await botClient.SendChatAction(chatId, global::Telegram.Bot.Types.Enums.ChatAction.Typing,
                    cancellationToken: cancellationToken);
                break;
            case "attention":
                if (!string.IsNullOrEmpty(_config.BotName))
                    await botClient.SetMyName(_config.BotName + _config.AttentionMarker,
                        cancellationToken: cancellationToken);
                break;
            case "attention_clear":
                if (!string.IsNullOrEmpty(_config.BotName))
                    await botClient.SetMyName(_config.BotName,
                        cancellationToken: cancellationToken);
                break;
        }
    }
}
