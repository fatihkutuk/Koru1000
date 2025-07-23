namespace Koru1000.Core.Models.ViewModels
{
    public class ChannelDeviceWithStatus
    {
        public int Id { get; set; }
        public string ChannelName { get; set; }
        public int DeviceTypeId { get; set; }
        public byte StatusCode { get; set; }
        public string StatusDefinition { get; set; }
        public string TypeName { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
    }

    public class DatabaseStats
    {
        public int TotalChannelDevices { get; set; }
        public int TotalChannelTypes { get; set; }
        public int TotalDeviceTypes { get; set; }
        public int TotalTagValues { get; set; }
        public int TotalWriteTags { get; set; }
        public Dictionary<byte, int> StatusCounts { get; set; } = new();
    }
}