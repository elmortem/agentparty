namespace AgentParty.File;

public class FileServerConfig
{
    public string Directory { get; set; } = string.Empty;
    public int PollingIntervalMs { get; set; } = 5000;
    public HashSet<string> AllowedCommands { get; set; } = new();
}
