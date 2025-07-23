using Koru1000.Core.Models.KbinModels;
using Dapper;

namespace Koru1000.DatabaseManager.Repositories
{
    public class TagRepository
    {
        private readonly DatabaseManager _dbManager;

        public TagRepository(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        public async Task<IEnumerable<TagOku>> GetLatestTagValuesAsync(int? deviceId = null, int limit = 1000)
        {
            string sql = @"
                SELECT devId, tagName, tagValue, readTime 
                FROM _tagoku";

            if (deviceId.HasValue)
            {
                sql += " WHERE devId = @DeviceId";
            }

            sql += " ORDER BY readTime DESC LIMIT @Limit";

            return await _dbManager.QueryKbinAsync<TagOku>(sql, new { DeviceId = deviceId, Limit = limit });
        }

        public async Task<IEnumerable<TagYaz>> GetPendingWriteTagsAsync(int? deviceId = null, int limit = 1000)
        {
            string sql = @"
                SELECT devId, tagName, tagValue, time 
                FROM _tagyaz";

            if (deviceId.HasValue)
            {
                sql += " WHERE devId = @DeviceId";
            }

            sql += " ORDER BY time DESC LIMIT @Limit";

            return await _dbManager.QueryKbinAsync<TagYaz>(sql, new { DeviceId = deviceId, Limit = limit });
        }

        public async Task<int> GetTagOkuCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM _tagoku";
            return await _dbManager.QueryFirstKbinAsync<int>(sql);
        }

        public async Task<int> GetTagYazCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM _tagyaz";
            return await _dbManager.QueryFirstKbinAsync<int>(sql);
        }

        public async Task<Dictionary<int, int>> GetTagCountsByDeviceAsync()
        {
            const string sql = @"
                SELECT devId, COUNT(*) as Count 
                FROM _tagoku 
                GROUP BY devId 
                ORDER BY devId";

            var results = await _dbManager.QueryKbinAsync<dynamic>(sql);
            return results.ToDictionary(x => (int)x.devId, x => (int)x.Count);
        }
    }
}