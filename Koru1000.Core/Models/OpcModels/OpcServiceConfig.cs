namespace Koru1000.Core.Models.OpcModels
{
    public class OpcServiceConfig
    {
        public string ServiceName { get; set; } = "Koru1000 OPC Service";
        public string ServiceDescription { get; set; } = "OPC UA Client Service for Industrial Data Collection";
        public int MaxConcurrentDrivers { get; set; } = 10;
        public int StatusCheckIntervalSeconds { get; set; } = 30;
        public ClientLimits Limits { get; set; } = new();
        public OpcSecuritySettings Security { get; set; } = new();
        public LoggingSettings Logging { get; set; } = new();
    }

    public class ClientLimits
    {
        public int MaxTagsPerSubscription { get; set; } = 20000;
        public int MaxChannelsPerSession { get; set; } = 50;
        public int MaxDevicesPerSession { get; set; } = 50;
        public int MaxSubscriptionsPerSession { get; set; } = 10;
        public int PublishingIntervalMs { get; set; } = 1000;
        public int MaxNotificationsPerPublish { get; set; } = 10000;
        public int SessionTimeoutMs { get; set; } = 300000; // 5 minutes
        public int ReconnectDelayMs { get; set; } = 5000;
        public int MaxReconnectAttempts { get; set; } = 5;
    }

    public class OpcSecuritySettings
    {
        public string ApplicationUri { get; set; } = "urn:localhost:Koru1000:OpcService";
        public string ApplicationName { get; set; } = "Koru1000 OPC Service";
        public int SecurityMode { get; set; } = 3; // SignAndEncrypt
        public string SecurityPolicy { get; set; } = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";
        public int UserTokenType { get; set; } = 0; // Anonymous
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string CertificateStorePath { get; set; } = "OPC Foundation/CertificateStores";
    }

    public class LoggingSettings
    {
        public bool EnableOpcTracing { get; set; } = true;
        public string LogLevel { get; set; } = "Information";
        public bool LogDataChanges { get; set; } = false; // Çok fazla log olabilir
        public bool LogConnectionStatus { get; set; } = true;
    }
}