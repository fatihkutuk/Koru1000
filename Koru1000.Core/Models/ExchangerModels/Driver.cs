namespace Koru1000.Core.Models.ExchangerModels
{
    public class Driver
    {
        public int Id { get; set; }
        public int DriverTypeId { get; set; }
        public string Name { get; set; }
        public string CustomSettings { get; set; }
    }

    public class DriverType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string CommonSettings { get; set; }
    }
}