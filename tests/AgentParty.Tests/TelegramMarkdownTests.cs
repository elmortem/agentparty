using AgentParty.Telegram;

namespace AgentParty.Tests;

public class TelegramMarkdownTests
{
    [Fact]
    public void Escape_EscapesSpecChars()
    {
        Assert.Equal(@"\*\_\[\`\\", TelegramMarkdown.Escape("*_[`\\"));
    }

    [Fact]
    public void Escape_LeavesOtherCharsAsIs()
    {
        var input = "Hello, world! 123 Привет 🎉 (test) 19.99";
        Assert.Equal(input, TelegramMarkdown.Escape(input));
    }

    [Fact]
    public void Escape_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TelegramMarkdown.Escape(""));
    }

    [Fact]
    public void Strip_RemovesMarkdownChars()
    {
        Assert.Equal("Hello world", TelegramMarkdown.Strip("*Hello* _world_"));
    }

    [Fact]
    public void Strip_LeavesNonMarkdownCharsAsIs()
    {
        var input = "Hello, world! 123";
        Assert.Equal(input, TelegramMarkdown.Strip(input));
    }

    [Fact]
    public void Strip_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TelegramMarkdown.Strip(""));
    }
}
