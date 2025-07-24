namespace Koru1000.KepServerService
{
    public class FastTagInfo
    {
        public int DeviceTagId { get; set; }
        public int DeviceId { get; set; }
        public string ChannelName { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string TagName { get; set; } = "";
        public string NodeId => $"ns=2;s={ChannelName}.{DeviceName}.{TagName}";
    }

    public class FastTagLoaderService
    {
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ILogger _logger;

        // ILogger<FastTagLoaderService> yerine sadece ILogger kullan
        public FastTagLoaderService(DatabaseManager.DatabaseManager dbManager, ILogger logger)
        {
            _dbManager = dbManager;
            _logger = logger;
        }

        public async Task<List<FastTagInfo>> GetDriverTagsAsync(int driverId)
        {
            try
            {
                _logger.LogInformation($"🚀 Fast loading tags for driver {driverId}");
                var startTime = DateTime.Now;

                const string sql = @"
                    SELECT dtt.id AS DeviceTagId, 
                           d.id AS DeviceId,
                           d.channelName AS ChannelName, 
                           CONCAT('Device_', d.id) AS DeviceName, 
                           JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName
                    FROM channeldevice d
                    INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                    WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61) 
                    
                    UNION ALL
                    
                    SELECT dit.id AS DeviceTagId, 
                           d.id AS DeviceId,
                           d.channelName AS ChannelName, 
                           CONCAT('Device_', d.id) AS DeviceName, 
                           JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName
                    FROM channeldevice d
                    INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                    WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
                    ORDER BY ChannelName, DeviceName, TagName";

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });

                var tags = new List<FastTagInfo>();
                foreach (var result in results)
                {
                    tags.Add(new FastTagInfo
                    {
                        DeviceTagId = (int)result.DeviceTagId,
                        DeviceId = (int)result.DeviceId,
                        ChannelName = result.ChannelName?.ToString() ?? "",
                        DeviceName = result.DeviceName?.ToString() ?? "",
                        TagName = result.TagName?.ToString() ?? ""
                    });
                }

                var loadTime = DateTime.Now - startTime;
                _logger.LogInformation($"✅ Loaded {tags.Count} tags in {loadTime.TotalMilliseconds:F0}ms");

                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load tags for driver {driverId}");
                return new List<FastTagInfo>();
            }
        }
    }
}