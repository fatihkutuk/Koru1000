using Dapper;
using Koru1000.Core.Models.ExchangerModels;

namespace Koru1000.DatabaseManager.Repositories
{
    public class ChannelDeviceRepository
    {
        private readonly DatabaseManager _dbManager;

        public ChannelDeviceRepository(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        public async Task<IEnumerable<ChannelDevice>> GetAllAsync()
        {
            const string sql = @"
                SELECT id, channelName, channelJson, deviceJson, deviceTypeId, 
                       statusCode, createTime, updateTime, individualTags 
                FROM channeldevice 
                ORDER BY id";

            return await _dbManager.QueryExchangerAsync<ChannelDevice>(sql);
        }

        public async Task<ChannelDevice> GetByIdAsync(int id)
        {
            const string sql = @"
                SELECT id, channelName, channelJson, deviceJson, deviceTypeId, 
                       statusCode, createTime, updateTime, individualTags 
                FROM channeldevice 
                WHERE id = @Id";

            return await _dbManager.QueryFirstExchangerAsync<ChannelDevice>(sql, new { Id = id });
        }

        public async Task<IEnumerable<ChannelDevice>> GetByStatusCodeAsync(byte statusCode)
        {
            const string sql = @"
                SELECT id, channelName, channelJson, deviceJson, deviceTypeId, 
                       statusCode, createTime, updateTime, individualTags 
                FROM channeldevice 
                WHERE statusCode = @StatusCode
                ORDER BY id";

            return await _dbManager.QueryExchangerAsync<ChannelDevice>(sql, new { StatusCode = statusCode });
        }

        public async Task<int> GetCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM channeldevice";
            return await _dbManager.QueryFirstExchangerAsync<int>(sql);
        }

        public async Task<Dictionary<byte, int>> GetStatusCodeCountsAsync()
        {
            const string sql = @"
                SELECT statusCode, COUNT(*) as Count 
                FROM channeldevice 
                GROUP BY statusCode";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);
            return results.ToDictionary(x => (byte)x.statusCode, x => (int)x.Count);
        }
    }
}