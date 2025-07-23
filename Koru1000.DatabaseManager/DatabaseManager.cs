using MySql.Data.MySqlClient;
using System.Data;
using Dapper;

namespace Koru1000.DatabaseManager
{
    public class DatabaseManager
    {
        private readonly string _exchangerConnectionString;
        private readonly string _kbinConnectionString;
        private static DatabaseManager _instance;
        private static readonly object _lock = new object();

        private DatabaseManager(string exchangerConnectionString, string kbinConnectionString)
        {
            _exchangerConnectionString = exchangerConnectionString;
            _kbinConnectionString = kbinConnectionString;
        }

        public static DatabaseManager Instance(string exchangerConn = null, string kbinConn = null)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                        _instance = new DatabaseManager(exchangerConn, kbinConn);
                }
            }
            return _instance;
        }

        // dbdataexchanger bağlantısı
        public IDbConnection CreateExchangerConnection()
        {
            return new MySqlConnection(_exchangerConnectionString);
        }

        // kbindb bağlantısı  
        public IDbConnection CreateKbinConnection()
        {
            return new MySqlConnection(_kbinConnectionString);
        }

        // Bağlantı test etme - DÜZELTİLDİ
        public async Task<bool> TestExchangerConnectionAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_exchangerConnectionString);
                await connection.OpenAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> TestKbinConnectionAsync()
        {
            try
            {
                using var connection = new MySqlConnection(_kbinConnectionString);
                await connection.OpenAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Genel query çalıştırma metodları
        public async Task<IEnumerable<T>> QueryExchangerAsync<T>(string sql, object parameters = null)
        {
            using var connection = CreateExchangerConnection();
            return await connection.QueryAsync<T>(sql, parameters);
        }

        public async Task<IEnumerable<T>> QueryKbinAsync<T>(string sql, object parameters = null)
        {
            using var connection = CreateKbinConnection();
            return await connection.QueryAsync<T>(sql, parameters);
        }

        public async Task<T> QueryFirstExchangerAsync<T>(string sql, object parameters = null)
        {
            using var connection = CreateExchangerConnection();
            return await connection.QueryFirstOrDefaultAsync<T>(sql, parameters);
        }

        public async Task<T> QueryFirstKbinAsync<T>(string sql, object parameters = null)
        {
            using var connection = CreateKbinConnection();
            return await connection.QueryFirstOrDefaultAsync<T>(sql, parameters);
        }

        public async Task<int> ExecuteExchangerAsync(string sql, object parameters = null)
        {
            using var connection = CreateExchangerConnection();
            return await connection.ExecuteAsync(sql, parameters);
        }

        public async Task<int> ExecuteKbinAsync(string sql, object parameters = null)
        {
            using var connection = CreateKbinConnection();
            return await connection.ExecuteAsync(sql, parameters);
        }
    }
}