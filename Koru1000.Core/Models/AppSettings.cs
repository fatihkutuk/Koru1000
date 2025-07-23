namespace Koru1000.Core.Models
{
    public class AppSettings
    {
        public DatabaseSettings Database { get; set; } = new();
    }

    public class DatabaseSettings
    {
        public string ExchangerServer { get; set; } = "localhost";
        public string ExchangerDatabase { get; set; } = "dbdataexchanger";
        public string ExchangerUsername { get; set; } = "root";
        public string ExchangerPassword { get; set; } = "";
        public int ExchangerPort { get; set; } = 3306;

        public string KbinServer { get; set; } = "localhost";
        public string KbinDatabase { get; set; } = "kbindb";
        public string KbinUsername { get; set; } = "root";
        public string KbinPassword { get; set; } = "";
        public int KbinPort { get; set; } = 3306;

        public string GetExchangerConnectionString()
        {
            return $"Server={ExchangerServer};Port={ExchangerPort};Database={ExchangerDatabase};Uid={ExchangerUsername};Pwd={ExchangerPassword};";
        }

        public string GetKbinConnectionString()
        {
            return $"Server={KbinServer};Port={KbinPort};Database={KbinDatabase};Uid={KbinUsername};Pwd={KbinPassword};";
        }
    }
}