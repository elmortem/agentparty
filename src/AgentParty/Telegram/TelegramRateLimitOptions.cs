namespace AgentParty.Telegram;

public class TelegramRateLimitOptions
{
    public int GlobalPerSecond { get; set; } = 30;
    public int PerChatPerSecond { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
}
