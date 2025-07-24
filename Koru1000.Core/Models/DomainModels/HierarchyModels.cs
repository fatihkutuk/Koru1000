namespace Koru1000.Core.Models.DomainModels
{
    public class SystemHierarchy
    {
        public List<DriverTypeModel> DriverTypes { get; set; } = new();
        public DateTime LoadedAt { get; set; }
        public int TotalDevices { get; set; }
        public int TotalTags { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new();
    }

    public class DriverTypeModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> CommonSettings { get; set; } = new();
        public List<DriverModel> Drivers { get; set; } = new();
    }

    public class DriverModel
    {
        public int Id { get; set; }
        public int DriverTypeId { get; set; }
        public string Name { get; set; }
        public string EndpointUrl { get; set; }
        public Dictionary<string, object> CustomSettings { get; set; } = new();
        public DriverStatus Status { get; set; } = DriverStatus.Stopped;
        public List<ChannelModel> Channels { get; set; } = new();

        // OPC specific
        public string Namespace { get; set; } = "2";
        public string ProtocolType { get; set; } = "OPC";
        public string AddressFormat { get; set; }
        public ConnectionSettings ConnectionSettings { get; set; } = new();
        public SecuritySettings SecuritySettings { get; set; } = new();
        public CredentialSettings Credentials { get; set; } = new();
    }

    public class ChannelModel
    {
        public string Name { get; set; }
        public int ChannelTypeId { get; set; }
        public string ChannelTypeName { get; set; }
        public Dictionary<string, object> ChannelJson { get; set; } = new();
        public List<DeviceModel> Devices { get; set; } = new();
    }

    public class DeviceModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ChannelName { get; set; }
        public int DeviceTypeId { get; set; }
        public string DeviceTypeName { get; set; }
        public byte StatusCode { get; set; }
        public string StatusDescription { get; set; }
        public Dictionary<string, object> DeviceJson { get; set; } = new();
        public DateTime LastUpdateTime { get; set; }
        public List<TagModel> Tags { get; set; } = new();
    }

    public class TagModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string NodeId { get; set; }
        public string DataType { get; set; }
        public bool IsWritable { get; set; }
        public bool IsIndividual { get; set; }
        public object CurrentValue { get; set; }
        public string Quality { get; set; } = "Unknown";
        public DateTime LastReadTime { get; set; }

        // Protocol specific
        public string FormattedAddress { get; set; }
        public Dictionary<string, object> ProtocolData { get; set; } = new();
    }

    public class ConnectionSettings
    {
        public int UpdateRate { get; set; } = 1000;
        public int PublishingInterval { get; set; } = 1000;
        public int SessionTimeout { get; set; } = 300000;
        public int MaxTagsPerSubscription { get; set; } = 20000;
    }

    public class SecuritySettings
    {
        public string Mode { get; set; } = "SignAndEncrypt";
        public string Policy { get; set; } = "Basic256Sha256";
        public string UserTokenType { get; set; } = "UserName";
    }

    public class CredentialSettings
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public enum DriverStatus
    {
        Stopped = 0,
        Starting = 1,
        Running = 2,
        Error = 3,
        Stopping = 4
    }
}