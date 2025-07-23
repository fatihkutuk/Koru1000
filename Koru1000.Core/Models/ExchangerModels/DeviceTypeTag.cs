namespace Koru1000.Core.Models.ExchangerModels
{
    public class DeviceTypeTag
    {
        public int Id { get; set; }
        public int DeviceTypeId { get; set; }
        public string TagJson { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
    }
}