namespace AgentParty.Telegram;

public class TelegramServerConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string BotName { get; set; } = string.Empty;
    public string AttentionMarker { get; set; } = " \ud83d\udccc";
    public HashSet<string> AllowedCommands { get; set; } = new();
    public HashSet<long> AllowedUserIds { get; set; } = new();
    public HashSet<long> FeedChatIds { get; set; } = new();
    public bool FeedDiscoveryMode { get; set; }
}
