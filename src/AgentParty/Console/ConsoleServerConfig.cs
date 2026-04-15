namespace AgentParty.Console;

public class ConsoleServerConfig
{
    public string ClientId { get; set; } = "console";
    public HashSet<string> AllowedCommands { get; set; } = new();
    public string[] StartupCommands { get; set; } = [];
}
