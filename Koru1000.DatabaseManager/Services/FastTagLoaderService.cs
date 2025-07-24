using Koru1000.Core.Models.KepServerModels;
using Microsoft.Extensions.Logging;

namespace Koru1000.DatabaseManager.Services
{
    public class FastTagLoaderService
    {
        private readonly DatabaseManager _dbManager;
        private readonly ILogger<FastTagLoaderService>? _logger;

        public FastTagLoaderService(DatabaseManager dbManager, ILogger<FastTagLoaderService>? logger = null)
        {
            _dbManager = dbManager;
            _logger = logger;
        }

        /// <summary>
        /// Driver ID'ye göre hızlı tag yükleme - 2-3 saniyede tamamlanır
        /// </summary>
        public async Task<List<FastTagInfo>> GetDriverTagsForSubscriptionAsync(int driverId, string protocolType = "OPC")
        {
            try
            {
                _logger?.LogInformation($"🚀 Fast loading subscription tags for driver {driverId}, protocol: {protocolType}");
                var startTime = DateTime.Now;

                // Protocol'e göre SQL'i customize et
                var sql = GetSqlByProtocol(protocolType);

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });

                var tags = new List<FastTagInfo>();
                foreach (var result in results)
                {
                    tags.Add(new FastTagInfo
                    {
                        DeviceTagId = (int)result.DeviceTagId,
                        DeviceId = int.Parse(result.DeviceName), // DeviceName = device ID
                        ChannelName = result.ChannelName?.ToString() ?? "",
                        DeviceName = result.DeviceName?.ToString() ?? "",
                        TagName = result.TagName?.ToString() ?? "",
                        Address = result.Address?.ToString() ?? "" // Modbus için
                    });
                }

                var loadTime = DateTime.Now - startTime;
                _logger?.LogInformation($"✅ Fast loaded {tags.Count} subscription tags in {loadTime.TotalMilliseconds:F0}ms");

                return tags;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"❌ Fast tag loading failed for driver {driverId}");
                return new List<FastTagInfo>();
            }
        }

        private string GetSqlByProtocol(string protocolType)
        {
            return protocolType.ToUpper() switch
            {
                "OPC" or "KEPSERVEREX" => GetOpcSubscriptionSql(),
                "MODBUS" => GetModbusSubscriptionSql(),
                "MQTT" => GetMqttSubscriptionSql(),
                _ => GetOpcSubscriptionSql() // Default OPC
            };
        }

        private string GetOpcSubscriptionSql()
        {
            // Sadece OPC için gerekli field'lar - çok hızlı
            return @"
                SELECT dtt.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       '' AS Address
                FROM channeldevice d
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61) 
                
                UNION ALL
                
                SELECT dit.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       '' AS Address
                FROM channeldevice d
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
                ORDER BY ChannelName, DeviceName, TagName";
        }

        private string GetModbusSubscriptionSql()
        {
            // Modbus için address de gerekli
            return @"
                SELECT dtt.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""servermain.TAG_ADDRESS""')) AS Address
                FROM channeldevice d
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61) 
                
                UNION ALL
                
                SELECT dit.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""servermain.TAG_ADDRESS""')) AS Address
                FROM channeldevice d
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
                ORDER BY ChannelName, DeviceName, TagName";
        }

        private string GetMqttSubscriptionSql()
        {
            // MQTT için topic gerekli
            return @"
                SELECT dtt.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""mqtt.TOPIC""')) AS Address
                FROM channeldevice d
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61) 
                
                UNION ALL
                
                SELECT dit.id AS DeviceTagId, 
                       d.channelName AS ChannelName, 
                       CONCAT(d.id) AS DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""mqtt.TOPIC""')) AS Address
                FROM channeldevice d
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
                ORDER BY ChannelName, DeviceName, TagName";
        }

        /// <summary>
        /// Status code değişikliklerini izle - hangi device'lar eklenecek/silinecek
        /// </summary>
        public async Task<List<KepConfigAction>> GetPendingConfigActionsAsync(int driverId)
        {
            try
            {
                const string sql = @"
                    SELECT cd.id as DeviceId, 
                           cd.channelName as ChannelName,
                           CONCAT('Device_', cd.id) as DeviceName,
                           cd.statusCode,
                           cd.updateTime as RequestTime
                    FROM channeldevice cd
                    WHERE cd.driverId = @DriverId 
                    AND cd.statusCode IN (10, 11, 20, 21, 31) -- Action kodları
                    ORDER BY cd.updateTime";

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });

                var actions = new List<KepConfigAction>();
                foreach (var result in results)
                {
                    var actionType = GetActionTypeFromStatusCode((byte)result.statusCode);
                    if (actionType.HasValue)
                    {
                        actions.Add(new KepConfigAction
                        {
                            DeviceId = (int)result.DeviceId,
                            DeviceName = result.DeviceName?.ToString() ?? "",
                            ChannelName = result.ChannelName?.ToString() ?? "",
                            ActionType = actionType.Value,
                            StatusCode = (byte)result.statusCode,
                            RequestTime = result.RequestTime
                        });
                    }
                }

                return actions;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to get pending config actions for driver {driverId}");
                return new List<KepConfigAction>();
            }
        }

        private KepActionType? GetActionTypeFromStatusCode(byte statusCode)
        {
            return statusCode switch
            {
                10 => KepActionType.AddChannel,
                11 => KepActionType.AddDevice,
                12 => KepActionType.AddTag,
                20 => KepActionType.DeleteChannel,
                21 => KepActionType.DeleteDevice,
                22 => KepActionType.DeleteTag,
                31 => KepActionType.UpdateDevice,
                _ => null
            };
        }
    }
}