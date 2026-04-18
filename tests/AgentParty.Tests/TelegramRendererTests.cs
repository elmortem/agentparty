using AgentParty.Content;
using AgentParty.Telegram;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentParty.Tests;

public class TelegramRendererTests
{
    private static TelegramRateLimiter NoThrottleLimiter() =>
        new(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100 });

    private class MarkdownFailingBotClient : ITelegramBotClient
    {
        public List<object> Requests { get; } = new();

        public long BotId => 0;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        public bool LocalBotServer => false;

        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var parseMode = request.GetType().GetProperty("ParseMode")?.GetValue(request);
            if (parseMode is ParseMode.Markdown)
                throw new ApiRequestException("can't parse entities", 400);

            Requests.Add(request);
            if (typeof(TResponse) == typeof(Message))
                return Task.FromResult((TResponse)(object)new Message());
            return Task.FromResult(default(TResponse)!);
        }

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private class RecordingBotClient : ITelegramBotClient
    {
        public List<object> Requests { get; } = new();

        public long BotId => 0;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        public bool LocalBotServer => false;

        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (typeof(TResponse) == typeof(Message))
                return Task.FromResult((TResponse)(object)new Message());
            return Task.FromResult(default(TResponse)!);
        }

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static ParseMode? GetParseMode(object request)
    {
        var val = request.GetType().GetProperty("ParseMode")?.GetValue(request);
        return val is ParseMode pm ? pm : null;
    }

    [Fact]
    public async Task RenderText_SendsWithMarkdownParseMode()
    {
        var bot = new RecordingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var msg = new AgentParty.Message { Type = MessageTypes.Text, Content = "Hello *world*" };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Single(bot.Requests);
        Assert.Equal(ParseMode.Markdown, GetParseMode(bot.Requests[0]));
    }

    [Fact]
    public async Task RenderChoice_SendsWithMarkdownParseMode_AndInlineKeyboard()
    {
        var bot = new RecordingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var choice = new ChoiceContent { Text = "Pick one", Options = ["A", "B"] };
        var msg = new AgentParty.Message { Type = MessageTypes.Choice, Content = choice.Serialize() };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Single(bot.Requests);
        Assert.Equal(ParseMode.Markdown, GetParseMode(bot.Requests[0]));
        // Verify inline keyboard is present
        var replyMarkup = bot.Requests[0].GetType().GetProperty("ReplyMarkup")?.GetValue(bot.Requests[0]);
        Assert.NotNull(replyMarkup);
    }

    [Fact]
    public async Task RenderListInfoItems_UsesMarkdownTitle()
    {
        var bot = new RecordingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var list = new ListContent { Title = "My List", Items = [new ListItem { Text = "item1" }] };
        var msg = new AgentParty.Message { Type = MessageTypes.List, Content = list.Serialize() };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Single(bot.Requests);
        Assert.Equal(ParseMode.Markdown, GetParseMode(bot.Requests[0]));
        var text = bot.Requests[0].GetType().GetProperty("Text")?.GetValue(bot.Requests[0]) as string;
        Assert.NotNull(text);
        Assert.Contains("*My List*", text);
    }

    [Fact]
    public async Task RenderListActionItems_EachSendsMarkdown()
    {
        var bot = new RecordingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var list = new ListContent
        {
            Items =
            [
                new ListItem { Id = "a", Text = "Item A", Actions = [new ListAction { Id = "go", Label = "Go" }] },
                new ListItem { Id = "b", Text = "Item B", Actions = [new ListAction { Id = "go", Label = "Go" }] }
            ]
        };
        var msg = new AgentParty.Message { Type = MessageTypes.List, Content = list.Serialize() };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Equal(2, bot.Requests.Count);
        foreach (var req in bot.Requests)
            Assert.Equal(ParseMode.Markdown, GetParseMode(req));
    }

    [Fact]
    public async Task RenderText_FallsBackToPlainText_OnMarkdownParseError()
    {
        var bot = new MarkdownFailingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var msg = new AgentParty.Message { Type = MessageTypes.Text, Content = "Hello *broken" };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Single(bot.Requests);
        Assert.NotEqual(ParseMode.Markdown, GetParseMode(bot.Requests[0]));
        var text = bot.Requests[0].GetType().GetProperty("Text")?.GetValue(bot.Requests[0]) as string;
        Assert.Equal("Hello broken", text);
    }

    [Fact]
    public async Task RenderNotificationThinking_SendsChatAction()
    {
        var bot = new RecordingBotClient();
        using var limiter = NoThrottleLimiter();
        var renderer = new TelegramRenderer(new TelegramServerConfig { BotToken = "t" });
        var notification = new NotificationContent { Kind = "thinking" };
        var msg = new AgentParty.Message { Type = MessageTypes.Notification, Content = notification.Serialize() };

        await renderer.RenderAsync(bot, limiter, 42L, msg);

        Assert.Single(bot.Requests);
        Assert.Contains("ChatAction", bot.Requests[0].GetType().Name);
    }
}
