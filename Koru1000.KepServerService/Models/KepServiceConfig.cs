namespace Koru1000.KepServerService.Models;

public class KepServiceConfig
{
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";
    public string KepServerServiceName { get; set; } = "KEPServerEX 6";
    public bool AutoRestartKepServer { get; set; } = true;
    public int KepServerRestartDelay { get; set; } = 10000;
    public string ConfigApiBaseUrl { get; set; } = "http://127.0.0.1:57412/config/v1/project";
    public SecurityConfig Security { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
}

public class SecurityConfig
{
    public string Username { get; set; } = "administrator";
    public string Password { get; set; } = "";
    public string Mode { get; set; } = "None";
    public string Policy { get; set; } = "None";
}

public class LimitsConfig
{
    public int SessionTimeoutMs { get; set; } = 600000;
    public int PublishingIntervalMs { get; set; } = 2000;
    public int MaxNotificationsPerPublish { get; set; } = 1000;
    public int MaxTagsPerClient { get; set; } = 30000;
}