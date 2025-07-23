namespace Koru1000.Core.Models.KbinModels
{
    public class TagOku
    {
        public int DevId { get; set; }
        public string TagName { get; set; }
        public double TagValue { get; set; }
        public DateTime ReadTime { get; set; }
    }

    public class TagYaz
    {
        public int DevId { get; set; }
        public string TagName { get; set; }
        public double TagValue { get; set; }
        public DateTime Time { get; set; }
    }
}