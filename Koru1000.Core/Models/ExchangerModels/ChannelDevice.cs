namespace Koru1000.Core.Models.ExchangerModels
{
    public class ChannelDevice
    {
        public int Id { get; set; }
        public string ChannelName { get; set; }
        public string ChannelJson { get; set; }
        public string DeviceJson { get; set; }
        public int DeviceTypeId { get; set; }
        public byte StatusCode { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public string IndividualTags { get; set; }
    }
}