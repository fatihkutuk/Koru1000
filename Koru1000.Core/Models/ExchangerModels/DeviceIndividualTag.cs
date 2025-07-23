namespace Koru1000.Core.Models.ExchangerModels
{
    public class DeviceIndividualTag
    {
        public int Id { get; set; }
        public int ChannelDeviceId { get; set; }
        public string TagJson { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
    }
}