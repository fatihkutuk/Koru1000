using Koru1000.Core.Models.ExchangerModels;
using Dapper;

namespace Koru1000.DatabaseManager.Repositories
{
    public class ChannelTypesRepository
    {
        private readonly DatabaseManager _dbManager;

        public ChannelTypesRepository(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        public async Task<IEnumerable<ChannelTypes>> GetAllAsync()
        {
            const string sql = @"
                SELECT id, name, channel, device, createTime 
                FROM channeltypes 
                ORDER BY id";

            return await _dbManager.QueryExchangerAsync<ChannelTypes>(sql);
        }

        public async Task<ChannelTypes> GetByIdAsync(int id)
        {
            const string sql = @"
                SELECT id, name, channel, device, createTime 
                FROM channeltypes 
                WHERE id = @Id";

            return await _dbManager.QueryFirstExchangerAsync<ChannelTypes>(sql, new { Id = id });
        }

        public async Task<int> GetCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM channeltypes";
            return await _dbManager.QueryFirstExchangerAsync<int>(sql);
        }
    }
}