using Koru1000.Core.Models.DomainModels;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Koru1000.DatabaseManager.Services
{
    public class SystemHierarchyService
    {
        private readonly DatabaseManager _dbManager;
        private readonly ILogger<SystemHierarchyService>? _logger;
        private SystemHierarchy? _cachedHierarchy;
        private readonly object _lockObject = new object();

        public SystemHierarchy? CurrentHierarchy => _cachedHierarchy;

        public SystemHierarchyService(DatabaseManager dbManager, ILogger<SystemHierarchyService>? logger = null)
        {
            _dbManager = dbManager;
            _logger = logger;
        }

        public async Task<SystemHierarchy> LoadCompleteHierarchyAsync(bool forceReload = false)
        {
            if (!forceReload && _cachedHierarchy != null)
            {
                _logger?.LogInformation("Returning cached hierarchy");
                return _cachedHierarchy;
            }

            lock (_lockObject)
            {
                if (!forceReload && _cachedHierarchy != null)
                    return _cachedHierarchy;
            }

            try
            {
                _logger?.LogInformation("🔄 Loading complete system hierarchy...");
                var startTime = DateTime.Now;

                var hierarchy = new SystemHierarchy
                {
                    LoadedAt = DateTime.Now
                };

                // 1. Driver Types yükle
                hierarchy.DriverTypes = await LoadDriverTypesAsync();

                // 2. Her Driver Type için Drivers yükle
                foreach (var driverType in hierarchy.DriverTypes)
                {
                    driverType.Drivers = await LoadDriversAsync(driverType.Id);

                    // 3. Her Driver için Channels yükle
                    foreach (var driver in driverType.Drivers)
                    {
                        driver.Channels = await LoadChannelsAsync(driver.Id);

                        // 4. Her Channel için Devices yükle
                        foreach (var channel in driver.Channels)
                        {
                            channel.Devices = await LoadDevicesAsync(driver.Id, channel.Name);
                            // TAG YOK! Çok hızlı yükleme için
                        }
                    }
                }

                // İstatistikleri hesapla
                CalculateStatistics(hierarchy);

                var loadTime = DateTime.Now - startTime;
                _logger?.LogInformation($"✅ Complete hierarchy loaded in {loadTime.TotalMilliseconds:F0}ms - {hierarchy.TotalDevices} devices, {hierarchy.TotalTags} tags");

                lock (_lockObject)
                {
                    _cachedHierarchy = hierarchy;
                }

                return hierarchy;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to load complete hierarchy");
                throw;
            }
        }

        private async Task<List<DriverTypeModel>> LoadDriverTypesAsync()
        {
            const string sql = @"
                SELECT id, name, commonSettings
                FROM drivertype
                ORDER BY name";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);
            var driverTypes = new List<DriverTypeModel>();

            foreach (var result in results)
            {
                var driverType = new DriverTypeModel
                {
                    Id = (int)result.id,
                    Name = result.name?.ToString() ?? "",
                    Description = result.name?.ToString() ?? ""
                };

                // Common settings parse et
                if (!string.IsNullOrEmpty(result.commonSettings?.ToString()))
                {
                    try
                    {
                        driverType.CommonSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            result.commonSettings.ToString()) ?? new Dictionary<string, object>();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Failed to parse common settings for driver type {driverType.Name}");
                    }
                }

                driverTypes.Add(driverType);
            }

            return driverTypes;
        }

        private async Task<List<DriverModel>> LoadDriversAsync(int driverTypeId)
        {
            const string sql = @"
                SELECT d.id, d.name, d.customSettings
                FROM driver d
                WHERE d.driverTypeId = @DriverTypeId
                ORDER BY d.name";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverTypeId = driverTypeId });
            var drivers = new List<DriverModel>();

            foreach (var result in results)
            {
                var driver = new DriverModel
                {
                    Id = (int)result.id,
                    DriverTypeId = driverTypeId,
                    Name = result.name?.ToString() ?? ""
                };

                // Custom settings parse et
                if (!string.IsNullOrEmpty(result.customSettings?.ToString()))
                {
                    try
                    {
                        ParseDriverCustomSettings(driver, result.customSettings.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Failed to parse custom settings for driver {driver.Name}");
                    }
                }

                drivers.Add(driver);
            }

            return drivers;
        }

        private void ParseDriverCustomSettings(DriverModel driver, string customSettingsJson)
        {
            using var doc = JsonDocument.Parse(customSettingsJson);
            var root = doc.RootElement;

            driver.CustomSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(customSettingsJson) ?? new();

            // EndpointUrl
            if (root.TryGetProperty("EndpointUrl", out var endpointUrl))
                driver.EndpointUrl = endpointUrl.GetString() ?? "";

            // Namespace
            if (root.TryGetProperty("namespace", out var ns))
                driver.Namespace = ns.GetString() ?? "2";

            // Protocol Type
            if (root.TryGetProperty("protocolType", out var pt))
                driver.ProtocolType = pt.GetString() ?? "OPC";

            // Address Format
            if (root.TryGetProperty("addressFormat", out var af))
                driver.AddressFormat = af.GetString() ?? "ns={namespace};s={channelName}.{deviceName}.{tagName}";

            // Connection Settings
            if (root.TryGetProperty("connectionSettings", out var cs))
            {
                if (cs.TryGetProperty("updateRate", out var ur))
                    driver.ConnectionSettings.UpdateRate = ur.GetInt32();
                if (cs.TryGetProperty("publishingInterval", out var pi))
                    driver.ConnectionSettings.PublishingInterval = pi.GetInt32();
                if (cs.TryGetProperty("sessionTimeout", out var st))
                    driver.ConnectionSettings.SessionTimeout = st.GetInt32();
                if (cs.TryGetProperty("maxTagsPerSubscription", out var mts))
                    driver.ConnectionSettings.MaxTagsPerSubscription = mts.GetInt32();
            }

            // Security Settings
            if (root.TryGetProperty("security", out var sec))
            {
                if (sec.TryGetProperty("mode", out var sm))
                    driver.SecuritySettings.Mode = sm.GetString() ?? "SignAndEncrypt";
                if (sec.TryGetProperty("policy", out var sp))
                    driver.SecuritySettings.Policy = sp.GetString() ?? "Basic256Sha256";
                if (sec.TryGetProperty("userTokenType", out var ut))
                    driver.SecuritySettings.UserTokenType = ut.GetString() ?? "UserName";
            }

            // Credentials
            if (root.TryGetProperty("credentials", out var cred))
            {
                if (cred.TryGetProperty("username", out var user))
                    driver.Credentials.Username = user.GetString() ?? "";
                if (cred.TryGetProperty("password", out var pass))
                    driver.Credentials.Password = pass.GetString() ?? "";
            }
        }

        private async Task<List<ChannelModel>> LoadChannelsAsync(int driverId)
        {
            const string sql = @"
                SELECT DISTINCT cd.channelName, 
                       COUNT(*) as deviceCount,
                       dt.ChannelTypeId,
                       ct.name as channelTypeName
                FROM channeldevice cd
                INNER JOIN devicetype dt ON cd.deviceTypeId = dt.id
                LEFT JOIN channeltypes ct ON dt.ChannelTypeId = ct.id
                WHERE cd.driverId = @DriverId
                GROUP BY cd.channelName, dt.ChannelTypeId, ct.name
                ORDER BY cd.channelName";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });
            var channels = new List<ChannelModel>();

            foreach (var result in results)
            {
                var channel = new ChannelModel
                {
                    Name = result.channelName?.ToString() ?? "",
                    ChannelTypeId = result.ChannelTypeId ?? 0,
                    ChannelTypeName = result.channelTypeName?.ToString() ?? "Unknown"
                };

                channels.Add(channel);
            }

            return channels;
        }

        private async Task<List<DeviceModel>> LoadDevicesAsync(int driverId, string channelName)
        {
            const string sql = @"
                SELECT cd.id, cd.channelName, cd.deviceTypeId, cd.statusCode, 
                       cd.deviceJson, cd.updateTime, dt.typeName, 
                       cds.statusDefinition
                FROM channeldevice cd
                LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
                LEFT JOIN channeldevicestatus cds ON cd.statusCode = cds.statusCode
                WHERE cd.driverId = @DriverId AND cd.channelName = @ChannelName
                ORDER BY cd.id";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql,
                new { DriverId = driverId, ChannelName = channelName });
            var devices = new List<DeviceModel>();

            foreach (var result in results)
            {
                var device = new DeviceModel
                {
                    Id = (int)result.id,
                    Name = $"Device_{result.id}",
                    ChannelName = channelName,
                    DeviceTypeId = result.deviceTypeId ?? 0,
                    DeviceTypeName = result.typeName?.ToString() ?? "Unknown",
                    StatusCode = (byte)result.statusCode,
                    StatusDescription = result.statusDefinition?.ToString() ?? "Unknown",
                    LastUpdateTime = result.updateTime
                };

                // Device JSON parse et
                if (!string.IsNullOrEmpty(result.deviceJson?.ToString()))
                {
                    try
                    {
                        device.DeviceJson = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            result.deviceJson.ToString()) ?? new Dictionary<string, object>();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Failed to parse device JSON for device {device.Id}");
                    }
                }

                // TAG YOK! Hızlı yükleme için
                device.Tags = new List<TagModel>();

                devices.Add(device);
            }

            return devices;
        }

        public async Task<List<TagModel>> LoadTagsForDeviceAsync(int deviceId, int deviceTypeId)
        {
            try
            {
                _logger?.LogInformation($"🏷️ Loading tags for device {deviceId}...");
                var startTime = DateTime.Now;

                var tags = await LoadTagsAsync(deviceId, deviceTypeId);

                var loadTime = DateTime.Now - startTime;
                _logger?.LogInformation($"✅ Loaded {tags.Count} tags for device {deviceId} in {loadTime.TotalMilliseconds:F0}ms");

                return tags;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to load tags for device {deviceId}");
                return new List<TagModel>();
            }
        }

        private async Task<List<TagModel>> LoadTagsAsync(int deviceId, int deviceTypeId)
        {
            var tags = new List<TagModel>();

            // Device Type Tags
            const string deviceTypeTagsSql = @"
                SELECT id, tagJson, 'DeviceType' as TagSource
                FROM devicetypetag 
                WHERE deviceTypeId = @DeviceTypeId
                ORDER BY id";

            var deviceTypeTags = await _dbManager.QueryExchangerAsync<dynamic>(
                deviceTypeTagsSql, new { DeviceTypeId = deviceTypeId });

            foreach (var result in deviceTypeTags)
            {
                var tag = ParseTagFromJson(result.id, result.tagJson?.ToString(), false);
                if (tag != null)
                {
                    // Current value'yu yükle
                    tag.CurrentValue = await GetCurrentTagValueAsync(deviceId, tag.Name);
                    tags.Add(tag);
                }
            }

            // Individual Tags
            const string individualTagsSql = @"
                SELECT id, tagJson, 'Individual' as TagSource
                FROM deviceindividualtag 
                WHERE channelDeviceId = @DeviceId
                ORDER BY id";

            var individualTags = await _dbManager.QueryExchangerAsync<dynamic>(
                individualTagsSql, new { DeviceId = deviceId });

            foreach (var result in individualTags)
            {
                var tag = ParseTagFromJson(result.id, result.tagJson?.ToString(), true);
                if (tag != null)
                {
                    tag.CurrentValue = await GetCurrentTagValueAsync(deviceId, tag.Name);
                    tags.Add(tag);
                }
            }

            return tags;
        }

        private TagModel? ParseTagFromJson(int tagId, string? tagJson, bool isIndividual)
        {
            if (string.IsNullOrWhiteSpace(tagJson))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(tagJson);
                var root = doc.RootElement;

                var tag = new TagModel
                {
                    Id = tagId,
                    IsIndividual = isIndividual,
                    Name = root.TryGetProperty("common.ALLTYPES_NAME", out var name) ?
                           name.GetString() ?? "Unknown" : "Unknown",
                    Address = root.TryGetProperty("servermain.TAG_ADDRESS", out var addr) ?
                             addr.GetString() ?? "" : "",
                    DataType = root.TryGetProperty("servermain.TAG_DATA_TYPE", out var type) ?
                              GetDataTypeName(type.GetInt32()) : "Unknown",
                    IsWritable = root.TryGetProperty("servermain.TAG_READ_WRITE_ACCESS", out var access) ?
                                access.GetInt32() != 0 : false,
                    Quality = "Unknown",
                    LastReadTime = DateTime.Now
                };

                return tag;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"Failed to parse tag JSON for tag {tagId}");
                return null;
            }
        }

        private async Task<object?> GetCurrentTagValueAsync(int deviceId, string tagName)
        {
            try
            {
                const string sql = @"
                    SELECT tagValue 
                    FROM kbindb._tagoku 
                    WHERE devId = @DeviceId AND tagName = @TagName 
                    ORDER BY readTime DESC 
                    LIMIT 1";

                var result = await _dbManager.QueryFirstKbinAsync<dynamic>(sql,
                    new { DeviceId = deviceId, TagName = tagName });

                return result?.tagValue;
            }
            catch
            {
                return null;
            }
        }

        private void CalculateStatistics(SystemHierarchy hierarchy)
        {
            hierarchy.TotalDevices = hierarchy.DriverTypes
                .SelectMany(dt => dt.Drivers)
                .SelectMany(d => d.Channels)
                .SelectMany(c => c.Devices)
                .Count();

            hierarchy.TotalTags = hierarchy.DriverTypes
                .SelectMany(dt => dt.Drivers)
                .SelectMany(d => d.Channels)
                .SelectMany(c => c.Devices)
                .SelectMany(dev => dev.Tags)
                .Count();

            hierarchy.Statistics["TotalDriverTypes"] = hierarchy.DriverTypes.Count;
            hierarchy.Statistics["TotalDrivers"] = hierarchy.DriverTypes.SelectMany(dt => dt.Drivers).Count();
            hierarchy.Statistics["TotalChannels"] = hierarchy.DriverTypes
                .SelectMany(dt => dt.Drivers)
                .SelectMany(d => d.Channels)
                .Count();
        }

        private string GetDataTypeName(int dataType)
        {
            return dataType switch
            {
                0 => "String",
                1 => "Boolean",
                2 => "Char",
                3 => "Byte",
                4 => "Short",
                5 => "Word",
                6 => "Long",
                7 => "DWord",
                8 => "Float",
                9 => "Double",
                10 => "Boolean",
                _ => $"Type_{dataType}"
            };
        }

        public void ClearCache()
        {
            lock (_lockObject)
            {
                _cachedHierarchy = null;
            }
            _logger?.LogInformation("Hierarchy cache cleared");
        }
    }
}