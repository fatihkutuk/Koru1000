using Koru1000.Core.Models.ExchangerModels;
using Koru1000.Core.Models.KbinModels;
using Koru1000.Core.Models.ViewModels;
using Koru1000.DatabaseManager.Repositories;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Koru1000.DatabaseManager.Services
{
    public class HierarchyService
    {
        private readonly DatabaseManager _dbManager;

        public HierarchyService(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        public async Task<ObservableCollection<TreeNodeBase>> BuildHierarchyAsync()
        {
            var rootNodes = new ObservableCollection<TreeNodeBase>();

            try
            {
                Console.WriteLine("=== BUILDING CORRECT HIERARCHY ===");

                // 1. DRIVER TYPES'lari al (KEPSERVEREX, OPC-UA, MODBUS TCP IP)
                var driverTypes = await GetDriverTypesAsync();
                Console.WriteLine($"Found {driverTypes.Count()} driver types");

                foreach (var driverType in driverTypes)
                {
                    Console.WriteLine($"Processing DriverType: {driverType.Name}");

                    var driverTypeNode = new DriverNode
                    {
                        Id = driverType.Id,
                        Name = driverType.Name,
                        DisplayName = $"🔌 {driverType.Name}",
                        DriverTypeName = driverType.Name,
                        IsExpanded = true
                    };

                    // 2. Bu Driver Type'a ait DRIVER'ları al
                    await LoadDriversForDriverType(driverTypeNode, driverType.Id);

                    rootNodes.Add(driverTypeNode);
                }

                Console.WriteLine($"Hierarchy built with {rootNodes.Count} driver types");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hierarchy build error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            return rootNodes;
        }

        private async Task<IEnumerable<DriverType>> GetDriverTypesAsync()
        {
            const string sql = @"
                SELECT id, name, commonSettings
                FROM drivertype
                ORDER BY name";

            return await _dbManager.QueryExchangerAsync<DriverType>(sql);
        }

        private async Task LoadDriversForDriverType(DriverNode driverTypeNode, int driverTypeId)
        {
            try
            {
                // Bu DriverType'a ait Driver'ları al
                const string sql = @"
                    SELECT id, name, customSettings
                    FROM driver
                    WHERE driverTypeId = @DriverTypeId
                    ORDER BY name";

                var drivers = await _dbManager.QueryExchangerAsync<Driver>(sql, new { DriverTypeId = driverTypeId });
                Console.WriteLine($"Found {drivers.Count()} drivers for driver type {driverTypeNode.Name}");

                foreach (var driver in drivers)
                {
                    var driverNode = new DriverNode
                    {
                        Id = driver.Id,
                        Name = driver.Name,
                        DisplayName = $"🔧 {driver.Name}",
                        DriverTypeName = driverTypeNode.DriverTypeName,
                        Parent = driverTypeNode,
                        IsExpanded = false
                    };

                    // 3. Bu Driver'a ait CHANNEL TYPES'ları al
                    await LoadChannelTypesForDriver(driverNode, driver.Id);

                    driverTypeNode.Children.Add(driverNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading drivers: {ex.Message}");
            }
        }

        private async Task LoadChannelTypesForDriver(DriverNode driverNode, int driverId)
        {
            try
            {
                // Bu Driver'a bağlı ChannelType'ları al (driver_channeltype_relation tablosundan)
                const string sql = @"
                    SELECT ct.id, ct.name, ct.channel, ct.device, ct.createTime,
                           dcr.maxCount, dcr.currentCount
                    FROM channeltypes ct
                    INNER JOIN driver_channeltype_relation dcr ON ct.id = dcr.channelTypeId
                    WHERE dcr.driverId = @DriverId
                    ORDER BY ct.name";

                var channelTypes = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });
                Console.WriteLine($"Found {channelTypes.Count()} channel types for driver {driverNode.Name}");

                foreach (var channelType in channelTypes)
                {
                    var channelTypeNode = new ChannelNode
                    {
                        Id = (int)channelType.id,
                        Name = channelType.name,
                        DisplayName = $"📂 {channelType.name}",
                        ChannelTypeId = (int)channelType.id,
                        ChannelTypeName = channelType.name,
                        ChannelJson = channelType.channel ?? "{}",
                        Parent = driverNode,
                        IsExpanded = false
                    };

                    // 4. Bu ChannelType'ı kullanan CHANNEL'ları al (aslında gruplandırılmış devices)
                    await LoadChannelsForChannelType(channelTypeNode, (int)channelType.id, channelType.name);

                    driverNode.Children.Add(channelTypeNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading channel types: {ex.Message}");
            }
        }

        private async Task LoadChannelsForChannelType(ChannelNode channelTypeNode, int channelTypeId, string channelTypeName)
        {
            try
            {
                // Bu ChannelType'ı kullanan benzersiz channel'ları al
                const string sql = @"
                    SELECT DISTINCT cd.channelName, COUNT(*) as deviceCount
                    FROM channeldevice cd
                    INNER JOIN devicetype dt ON cd.deviceTypeId = dt.id
                    WHERE dt.ChannelTypeId = @ChannelTypeId
                    GROUP BY cd.channelName
                    ORDER BY cd.channelName";

                var channels = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { ChannelTypeId = channelTypeId });
                Console.WriteLine($"Found {channels.Count()} unique channels for channel type {channelTypeName}");

                foreach (var channel in channels)
                {
                    var channelNode = new ChannelNode
                    {
                        Id = channelTypeId, // Channel'ın kendine özgü ID'si yok, ChannelType ID'sini kullanıyoruz
                        Name = channel.channelName ?? "Unknown Channel",
                        DisplayName = $"📡 {channel.channelName} ({channel.deviceCount} devices)",
                        ChannelTypeId = channelTypeId,
                        ChannelTypeName = channelTypeName,
                        Parent = channelTypeNode,
                        IsExpanded = false
                    };

                    // 5. Bu Channel'a ait DEVICE'ları al
                    await LoadDevicesForChannel(channelNode, channelTypeId, channel.channelName);

                    channelTypeNode.Children.Add(channelNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading channels: {ex.Message}");
            }
        }

        private async Task LoadDevicesForChannel(ChannelNode channelNode, int channelTypeId, string channelName)
        {
            try
            {
                // Bu Channel'daki Device'ları al
                const string sql = @"
                    SELECT cd.id, cd.channelName, cd.deviceTypeId, cd.statusCode, 
                           cd.deviceJson, cd.updateTime, dt.typeName, cds.statusDefinition
                    FROM channeldevice cd
                    LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
                    LEFT JOIN channeldevicestatus cds ON cd.statusCode = cds.statusCode
                    WHERE cd.channelName = @ChannelName 
                      AND dt.ChannelTypeId = @ChannelTypeId
                    ORDER BY cd.id";

                var devices = await _dbManager.QueryExchangerAsync<dynamic>(sql,
                    new { ChannelName = channelName, ChannelTypeId = channelTypeId });
                Console.WriteLine($"Found {devices.Count()} devices for channel {channelName}");

                foreach (var device in devices)
                {
                    var deviceNode = new DeviceNode
                    {
                        Id = (int)device.id,
                        Name = $"Device_{device.id}",
                        DisplayName = $"🔧 Device_{device.id} [{GetStatusDescription((byte)device.statusCode)}]",
                        DeviceTypeId = device.deviceTypeId ?? 0,
                        DeviceTypeName = device.typeName ?? "Unknown",
                        StatusCode = (byte)device.statusCode,
                        StatusDescription = device.statusDefinition ?? "Unknown",
                        DeviceJson = device.deviceJson ?? "{}",
                        LastUpdateTime = device.updateTime,
                        Parent = channelNode,
                        IsExpanded = false
                    };

                    // 6. Bu Device'a ait TAG'leri al
                    await LoadTagsForDevice(deviceNode, (int)device.id, device.deviceTypeId ?? 0);

                    channelNode.Children.Add(deviceNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading devices: {ex.Message}");
            }
        }

        private async Task LoadTagsForDevice(DeviceNode deviceNode, int deviceId, int deviceTypeId)
        {
            try
            {
                // Device Type Tags (genel tag'ler)
                const string deviceTypeTagsSql = @"
                    SELECT id, tagJson
                    FROM devicetypetag 
                    WHERE deviceTypeId = @DeviceTypeId
                    ORDER BY id
                    LIMIT 10"; // İlk 10 tag'i al

                var deviceTypeTags = await _dbManager.QueryExchangerAsync<DeviceTypeTag>(
                    deviceTypeTagsSql, new { DeviceTypeId = deviceTypeId });

                foreach (var tag in deviceTypeTags)
                {
                    var tagData = ParseTagJson(tag.TagJson);
                    if (tagData != null)
                    {
                        var tagNode = new TagNode
                        {
                            Id = tag.Id,
                            Name = tagData.Name,
                            DisplayName = $"🏷️ {tagData.Name} [{tagData.DataType}]",
                            TagAddress = tagData.Address,
                            DataType = tagData.DataType,
                            CurrentValue = await GetCurrentTagValue(deviceId, tagData.Name),
                            Parent = deviceNode,
                            Quality = "Good",
                            IsWritable = tagData.IsWritable
                        };

                        deviceNode.Children.Add(tagNode);
                    }
                }

                Console.WriteLine($"Added {deviceNode.Children.Count} tags for device {deviceId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading tags for device {deviceId}: {ex.Message}");
            }
        }

        private async Task<object> GetCurrentTagValue(int deviceId, string tagName)
        {
            try
            {
                const string sql = @"
                    SELECT tagValue 
                    FROM _tagoku 
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

        private TagInfo ParseTagJson(string tagJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(tagJson);
                var root = doc.RootElement;

                return new TagInfo
                {
                    Name = root.TryGetProperty("common.ALLTYPES_NAME", out var name) ?
                           name.GetString() : "Unknown",
                    Address = root.TryGetProperty("servermain.TAG_ADDRESS", out var addr) ?
                             addr.GetString() : "",
                    DataType = root.TryGetProperty("servermain.TAG_DATA_TYPE", out var type) ?
                              GetDataTypeName(type.GetInt32()) : "Unknown",
                    IsWritable = root.TryGetProperty("servermain.TAG_READ_WRITE_ACCESS", out var access) ?
                                access.GetInt32() != 0 : false
                };
            }
            catch
            {
                return null;
            }
        }

        private string GetDataTypeName(int dataType)
        {
            return dataType switch
            {
                1 => "Word",
                2 => "Short",
                3 => "DWord",
                4 => "Long",
                5 => "Float",
                8 => "Double",
                9 => "String",
                _ => $"Type_{dataType}"
            };
        }

        private string GetStatusDescription(byte statusCode)
        {
            return statusCode switch
            {
                11 => "Active",
                31 => "Connected",
                41 => "Running",
                51 => "Stopped",
                61 => "Online",
                _ => "Unknown"
            };
        }

        private class TagInfo
        {
            public string Name { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public bool IsWritable { get; set; }
        }
    }
}