namespace Koru1000.Core.Models.KepServerModels
{
    public class KepServerDriver
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        public string ConfigApiUrl { get; set; }
        public string OpcEndpoint { get; set; }
        public KepCredentials Credentials { get; set; } = new();
        public List<KepChannel> Channels { get; set; } = new();
        public KepDriverStatus Status { get; set; } = KepDriverStatus.Stopped;
    }

    public class KepCredentials
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string ConfigApiToken { get; set; } = "";
    }

    public class KepChannel
    {
        public int ChannelId { get; set; }
        public string ChannelName { get; set; }
        public List<KepDevice> Devices { get; set; } = new();
    }

    public class KepDevice
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string ChannelName { get; set; }
        public List<KepTag> Tags { get; set; } = new();
    }

    public class KepTag
    {
        public int TagId { get; set; }
        public string TagName { get; set; }
        public string NodeId { get; set; }
        public string Address { get; set; }
        public string DataType { get; set; }
        public bool IsWritable { get; set; }
    }

    public class KepConfigAction
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string ChannelName { get; set; }
        public KepActionType ActionType { get; set; }
        public byte StatusCode { get; set; }
        public DateTime RequestTime { get; set; }
    }

    public enum KepActionType
    {
        AddChannel = 10,
        AddDevice = 11,
        AddTag = 12,
        DeleteChannel = 20,
        DeleteDevice = 21,
        DeleteTag = 22,
        UpdateDevice = 31
    }

    public enum KepDriverStatus
    {
        Stopped = 0,
        Starting = 1,
        Running = 2,
        Error = 3,
        ConfiguringKep = 4
    }

    // Fast subscription için minimal tag info
    public class FastTagInfo
    {
        public int DeviceTagId { get; set; }
        public int DeviceId { get; set; }
        public string ChannelName { get; set; }
        public string DeviceName { get; set; }
        public string TagName { get; set; }
        public string Address { get; set; } // Modbus için
        public string NodeId => $"ns=2;{ChannelName}.{DeviceName}.{TagName}";
    }
}