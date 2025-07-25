namespace Koru1000.KepServerService.Models;

public class KepServiceConfig
{
    public string ServiceName { get; set; } = "Koru1000 KEP Server Service";
    public string ServiceDescription { get; set; } = "KEP Server OPC UA Client Service";
    public int MaxConcurrentClients { get; set; } = 5;
    public int DevicesPerClient { get; set; } = 1000;
    public int StatusCheckIntervalSeconds { get; set; } = 30;
    public bool RestartServiceOnError { get; set; } = true;
    public string KepServerServiceName { get; set; } = "KEPServerEXV6";
    public bool AutoRestartKepServer { get; set; } = true;
    public int KepServerRestartDelay { get; set; } = 15000;

    public KepClientLimits Limits { get; set; } = new();
    public KepSecuritySettings Security { get; set; } = new();
    public KepConnectionSettings Connection { get; set; } = new();
    public KepLoggingSettings Logging { get; set; } = new();
}

public class KepClientLimits
{
    public int MaxTagsPerClient { get; set; } = 20000;
    public int MaxDevicesPerClient { get; set; } = 1000;
    public int PublishingIntervalMs { get; set; } = 2000;
    public int MaxNotificationsPerPublish { get; set; } = 10000;
    public int SessionTimeoutMs { get; set; } = 360000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectAttempts { get; set; } = 5;
}

public class KepSecuritySettings
{
    public bool UseSecureConnection { get; set; } = true;
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;
    public string SecurityMode { get; set; } = "SignAndEncrypt"; // None, Sign, SignAndEncrypt
    public string SecurityPolicy { get; set; } = "Basic256Sha256";
    public string UserTokenType { get; set; } = "Anonymous"; // Anonymous, UserName
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class KepConnectionSettings
{
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";
    public int ConnectTimeoutMs { get; set; } = 15000;
    public int KeepAliveInterval { get; set; } = 10000;
    public int ReconnectPeriod { get; set; } = 5000;
}

public class KepLoggingSettings
{
    public bool EnableOpcTracing { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
    public bool LogDataChanges { get; set; } = false;
    public bool LogConnectionStatus { get; set; } = true;
    public bool LogPerformanceMetrics { get; set; } = true;
}