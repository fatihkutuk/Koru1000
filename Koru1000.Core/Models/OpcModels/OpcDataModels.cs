namespace Koru1000.Core.Models.OpcModels
{
    public class OpcDriverInfo
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        public string DriverType { get; set; }
        public string EndpointUrl { get; set; }
        public bool IsEnabled { get; set; }
        public Dictionary<string, object> CustomSettings { get; set; } = new();
        public List<int> ChannelTypeIds { get; set; } = new();

        // KEP ServerEX özel alanları
        public string Namespace { get; set; } = "2";
        public string ProtocolType { get; set; } = "OPC";
        public string AddressFormat { get; set; } = "ns={namespace};s={channelName}.{deviceName}.{tagName}";
        public KepConnectionSettings ConnectionSettings { get; set; } = new();
        public KepSecuritySettings Security { get; set; } = new();
        public KepCredentials Credentials { get; set; } = new(); // EKLE
    }
    public class KepCredentials
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
    public class KepConnectionSettings
    {
        public int UpdateRate { get; set; } = 1000;
        public int GroupDeadband { get; set; } = 0;
        public int SessionTimeout { get; set; } = 600000;  // 10 dakika
        public int PublishingInterval { get; set; } = 2000;  // 2 saniye
        public int MaxTagsPerSubscription { get; set; } = 25000;  // ✅ Yeni - Config'den gelecek
        public int ReconnectDelay { get; set; } = 5000;
        public int MaxReconnectAttempts { get; set; } = 5;

        // ✅ Yeni - Advanced Settings
        public int KeepAliveCount { get; set; } = 10;
        public int LifetimeCount { get; set; } = 100;
        public int MaxNotificationsPerPublish { get; set; } = 10000;
        public int QueueSize { get; set; } = 1;
        public bool DiscardOldest { get; set; } = true;

    }
    public class KepSecuritySettings
    {
        public string Mode { get; set; } = "SignAndEncrypt"; // Değiştir
        public string Policy { get; set; } = "Basic256Sha256"; // Değiştir
        public string UserTokenType { get; set; } = "UserName"; // Değiştir
    }
    public class OpcTagInfo
    {
        public int TagId { get; set; }
        public int DeviceId { get; set; }
        public string ChannelName { get; set; }
        public string TagName { get; set; }
        public string NodeId { get; set; }
        public string DataType { get; set; }
        public bool IsWritable { get; set; }
        public string TagAddress { get; set; }
    }

    public class OpcDataChangedEventArgs : EventArgs
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        public List<OpcTagValue> TagValues { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    public class OpcTagValue
    {
        public int DeviceId { get; set; }
        public string TagName { get; set; }
        public object Value { get; set; }
        public string Quality { get; set; }
        public DateTime SourceTimestamp { get; set; }
        public DateTime ServerTimestamp { get; set; }
    }

    public class OpcStatusChangedEventArgs : EventArgs
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        public OpcConnectionStatus Status { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum OpcConnectionStatus
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Error = 3,
        Reconnecting = 4
    }

    public class OpcServiceStatus
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        public string EndpointUrl { get; set; }
        public OpcConnectionStatus ConnectionStatus { get; set; }
        public DateTime LastConnected { get; set; }
        public DateTime LastDataReceived { get; set; }
        public int TotalTagsSubscribed { get; set; }
        public int ActiveSubscriptions { get; set; }
        public long TotalMessagesReceived { get; set; }
        public long TotalMessagesProcessed { get; set; }
        public string LastError { get; set; }
        public DateTime StatusTimestamp { get; set; }
    }

}