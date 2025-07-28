namespace Koru1000.KepServerService.Models;

public class KepTagInfo
{
    public int DeviceTagId { get; set; }
    public int DeviceId { get; set; }
    public string ChannelName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string TagName { get; set; } = "";
}

public class KepDataChangedEventArgs : EventArgs
{
    public int ClientId { get; set; }
    public int DeviceId { get; set; }
    public int DeviceTagId { get; set; }
    public string TagName { get; set; } = "";
    public object? Value { get; set; }
    public DateTime Timestamp { get; set; }
    public string Quality { get; set; } = "";
}

public class KepStatusChangedEventArgs : EventArgs
{
    public int ClientId { get; set; }
    public KepConnectionStatus Status { get; set; }
    public DateTime LastDataReceived { get; set; }
    public long TotalMessagesReceived { get; set; }
    public long TotalMessagesProcessed { get; set; }
    public string LastError { get; set; } = "";
    public int MonitoredItemCount { get; set; }
}

public enum KepConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Error = 3,
    Reconnecting = 4
}

public class DeviceOperationLock
{
    public int DeviceId { get; set; }
    public byte Status { get; set; }
    public DateTime StartTime { get; set; }
    public bool IsProcessing { get; set; }
}