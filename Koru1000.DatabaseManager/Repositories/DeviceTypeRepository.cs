using Koru1000.Core.Models.ExchangerModels;
using Dapper;

namespace Koru1000.DatabaseManager.Repositories
{
    public class DeviceTypeRepository
    {
        private readonly DatabaseManager _dbManager;

        public DeviceTypeRepository(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        public async Task<IEnumerable<DeviceType>> GetAllAsync()
        {
            const string sql = @"
                SELECT id, ChannelTypeId, typeName, description, 
                       createTime, updateTime, allTagJsons 
                FROM devicetype 
                ORDER BY id";

            return await _dbManager.QueryExchangerAsync<DeviceType>(sql);
        }

        public async Task<DeviceType> GetByIdAsync(int id)
        {
            const string sql = @"
                SELECT id, ChannelTypeId, typeName, description, 
                       createTime, updateTime, allTagJsons 
                FROM devicetype 
                WHERE id = @Id";

            return await _dbManager.QueryFirstExchangerAsync<DeviceType>(sql, new { Id = id });
        }

        public async Task<int> GetCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM devicetype";
            return await _dbManager.QueryFirstExchangerAsync<int>(sql);
        }

        public async Task<IEnumerable<DeviceTypeTag>> GetTagsByDeviceTypeIdAsync(int deviceTypeId)
        {
            const string sql = @"
                SELECT id, deviceTypeId, tagJson, createTime, updateTime 
                FROM devicetypetag 
                WHERE deviceTypeId = @DeviceTypeId
                ORDER BY id";

            return await _dbManager.QueryExchangerAsync<DeviceTypeTag>(sql, new { DeviceTypeId = deviceTypeId });
        }
    }
}