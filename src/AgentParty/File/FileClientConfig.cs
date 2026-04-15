namespace AgentParty.File;

public class FileClientConfig
{
    public string Directory { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public int PollingIntervalMs { get; set; } = 5000;
}
