using AgentParty.Content;
using AgentParty.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentParty.Tests;

public class TelegramServerRoutingTests
{
    private static TelegramServer MakeServer(TelegramServerConfig config) => new(config);

    private static User DummyUser(long id = 1) =>
        new() { Id = id, IsBot = false, FirstName = "Test" };

    private static Update PrivateMsg(long chatId, long userId, string text) =>
        new()
        {
            Message = new()
            {
                Chat = new() { Id = chatId, Type = ChatType.Private },
                From = DummyUser(userId),
                Text = text,
                Date = DateTime.UtcNow
            }
        };

    private static Update GroupMsg(long chatId, string text, int? threadId = null) =>
        new()
        {
            Message = new()
            {
                Chat = new() { Id = chatId, Type = ChatType.Group },
                Text = text,
                Date = DateTime.UtcNow,
                MessageThreadId = threadId
            }
        };

    private static Update ChanPost(long chatId, string? text = null, string? caption = null, Document? document = null) =>
        new()
        {
            ChannelPost = new()
            {
                Chat = new() { Id = chatId, Type = ChatType.Channel },
                Text = text,
                Caption = caption,
                Document = document,
                Date = DateTime.UtcNow
            }
        };

    private static Update CallbackUpd(long chatId, string data) =>
        new()
        {
            CallbackQuery = new()
            {
                Id = "cq-1",
                From = DummyUser(),
                Data = data,
                Message = new()
                {
                    Chat = new() { Id = chatId, Type = ChatType.Private },
                    Date = DateTime.UtcNow
                }
            }
        };

    // MessageId defaults to 0 in all test updates (read-only in Telegram.Bot 22.x)
    private const int DefaultMsgId = 0;

    [Fact]
    public async Task PrivateChat_FromAllowedUser_FiresMessageReceived()
    {
        var config = new TelegramServerConfig { BotToken = "t", AllowedUserIds = { 100L } };
        var server = MakeServer(config);
        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, PrivateMsg(1, 100, "hello"), default);

        Assert.NotNull(received);
        Assert.Equal("hello", received.Content);
    }

    [Fact]
    public async Task PrivateChat_FromDisallowedUser_Dropped()
    {
        var config = new TelegramServerConfig { BotToken = "t", AllowedUserIds = { 1L } };
        var server = MakeServer(config);
        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, PrivateMsg(1, 2, "hello"), default);

        Assert.Null(received);
    }

    [Fact]
    public async Task PrivateChat_EmptyAllowedUserIds_AcceptsAll()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, PrivateMsg(1, 999, "hello"), default);

        Assert.NotNull(received);
    }

    [Fact]
    public async Task GroupChat_NotInFeedSources_Dropped()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedSources = { new FeedSource(999, null) } };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, GroupMsg(1, "hello"), default);

        Assert.Null(received);
    }

    [Fact]
    public async Task GroupChat_InFeedSources_FiresFeedReceived()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedSources = { new FeedSource(1, null) } };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, GroupMsg(1, "hello"), default);

        Assert.NotNull(received);
        Assert.Equal("hello", received.Content);
    }

    [Fact]
    public async Task GroupChat_WithThreadId_DoesNotMatchNoThreadFeedSource()
    {
        // FeedSource(chatId, null) must NOT match a message with threadId set
        var config = new TelegramServerConfig { BotToken = "t", FeedSources = { new FeedSource(1, null) } };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, GroupMsg(1, "hello", threadId: 2736), default);

        Assert.Null(received);
    }

    [Fact]
    public async Task GroupChat_FeedDiscoveryMode_AcceptsAll()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedDiscoveryMode = true };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, GroupMsg(42, "hello"), default);

        Assert.NotNull(received);
    }

    [Fact]
    public async Task ChannelPost_FiresFeedReceived()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedDiscoveryMode = true };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, ChanPost(1, text: "channel news"), default);

        Assert.NotNull(received);
        Assert.Equal("channel news", received.Content);
    }

    [Fact]
    public async Task CallbackQuery_WithKnownMapping_UsesAgentPartyId()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        server.SentMessages.Set("1", DefaultMsgId, "confirm-001");

        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, CallbackUpd(1, "yes"), default);

        Assert.NotNull(received);
        var resp = ResponseContent.Parse(received.Content);
        Assert.Equal("confirm-001", resp.To);
        Assert.Equal("yes", resp.Value);
    }

    [Fact]
    public async Task CallbackQuery_WithoutMapping_IsSilentlyIgnored()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        IMessage? received = null;
        server.MessageReceived += m => received = m;

        var ex = await Record.ExceptionAsync(() => server.HandleUpdateAsync(null!, CallbackUpd(1, "yes"), default));

        Assert.Null(ex);
        Assert.Null(received);
    }

    [Fact]
    public async Task CallbackQuery_AfterClearForClient_IsSilentlyIgnored()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        server.SentMessages.Set("1", DefaultMsgId, "confirm-001");
        server.ClearSentMessagesForClient("1");

        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, CallbackUpd(1, "yes"), default);

        Assert.Null(received);
    }

    [Fact]
    public async Task CallbackQuery_ClearDoesNotAffectOtherClient()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        server.SentMessages.Set("1", DefaultMsgId, "msg-a");
        server.SentMessages.Set("2", DefaultMsgId, "msg-b");

        server.ClearSentMessagesForClient("1");

        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, CallbackUpd(2, "ok"), default);

        Assert.NotNull(received);
        var resp = ResponseContent.Parse(received.Content);
        Assert.Equal("msg-b", resp.To);
    }

    [Fact]
    public async Task CallbackQuery_SameButtonPressedTwice_BothFireResponse()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        server.SentMessages.Set("1", DefaultMsgId, "confirm-001");

        var responses = new List<IMessage>();
        server.MessageReceived += m => responses.Add(m);

        await server.HandleUpdateAsync(null!, CallbackUpd(1, "yes"), default);
        await server.HandleUpdateAsync(null!, CallbackUpd(1, "yes"), default);

        Assert.Equal(2, responses.Count);
    }

    [Fact]
    public async Task CallbackQuery_ListActionFormat_ParsedCorrectly()
    {
        var config = new TelegramServerConfig { BotToken = "t" };
        var server = MakeServer(config);
        server.SentMessages.Set("1", DefaultMsgId, "list-msg");

        IMessage? received = null;
        server.MessageReceived += m => received = m;

        await server.HandleUpdateAsync(null!, CallbackUpd(1, "item1:create"), default);

        Assert.NotNull(received);
        var resp = ResponseContent.Parse(received.Content);
        Assert.Equal("list-msg", resp.To);
        Assert.NotNull(resp.Items);
        Assert.Single(resp.Items);
        Assert.Equal("item1", resp.Items[0].Id);
        Assert.Equal("create", resp.Items[0].Action);
    }

    [Fact]
    public async Task FeedUpdate_NoText_Dropped()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedDiscoveryMode = true };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, ChanPost(1), default);

        Assert.Null(received);
    }

    [Fact]
    public async Task FeedUpdate_FallsBackToCaption()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedDiscoveryMode = true };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        await server.HandleUpdateAsync(null!, ChanPost(1, caption: "caption text"), default);

        Assert.NotNull(received);
        Assert.Equal("caption text", received.Content);
    }

    [Fact]
    public async Task FeedUpdate_FallsBackToDocumentFileName()
    {
        var config = new TelegramServerConfig { BotToken = "t", FeedDiscoveryMode = true };
        var server = MakeServer(config);
        IFeedMessage? received = null;
        server.FeedReceived += f => received = f;

        var doc = new Document { FileId = "f", FileUniqueId = "u", FileName = "report.pdf" };
        await server.HandleUpdateAsync(null!, ChanPost(1, document: doc), default);

        Assert.NotNull(received);
        Assert.Equal("report.pdf", received.Content);
    }
}
