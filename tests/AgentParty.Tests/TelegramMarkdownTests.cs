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
}
