namespace Koru1000.Core.Models.ExchangerModels
{
    public class DeviceType
    {
        public int Id { get; set; }
        public int? ChannelTypeId { get; set; }
        public string TypeName { get; set; }
        public string Description { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public string AllTagJsons { get; set; }
    }
}