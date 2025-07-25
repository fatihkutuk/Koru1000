namespace Koru1000.KepServerService.Models;

public class KepTagInfo
{
    public int DeviceTagId { get; set; }
    public int DeviceId { get; set; }
    public string ChannelName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string TagName { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsWritable { get; set; }
}

public class KepWriteTag
{
    public string ChannelName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string TagName { get; set; } = "";
    public object Value { get; set; } = 0;
    public string NodeId => $"ns=2;s={ChannelName}.{DeviceName}.{TagName}";
}

public class KepDeviceInfo
{
    public int DeviceId { get; set; }
    public string ChannelName { get; set; } = "";
    public string DeviceJson { get; set; } = "";
    public string ChannelJson { get; set; } = "";
    public int StatusCode { get; set; }
    public int ClientId { get; set; }
}

public class KepClientStatus
{
    public int ClientId { get; set; }
    public string Status { get; set; } = ""; // "Ok", "Bad"
    public DateTime LastUpdate { get; set; }
    public long TotalMessagesReceived { get; set; }
    public long TotalMessagesProcessed { get; set; }
    public int ActiveSubscriptions { get; set; }
    public int TotalTags { get; set; }
    public string LastError { get; set; } = "";
}

public enum KepConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Error = 3,
    Reconnecting = 4
}

public class KepDataChangedEventArgs : EventArgs
{
    public int ClientId { get; set; }
    public List<KepTagValue> TagValues { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public class KepTagValue
{
    public int DeviceId { get; set; }
    public string TagName { get; set; } = "";
    public object Value { get; set; } = 0;
    public string Quality { get; set; } = "";
    public DateTime SourceTimestamp { get; set; }
    public DateTime ServerTimestamp { get; set; }
}

public class KepStatusChangedEventArgs : EventArgs
{
    public int ClientId { get; set; }
    public KepConnectionStatus Status { get; set; }
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
}