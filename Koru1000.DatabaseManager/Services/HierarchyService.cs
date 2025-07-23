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

        #region Main Methods

        /// <summary>
        /// İlk yükleme - sadece Driver Type'ları yükle (Lazy Loading)
        /// </summary>
        public async Task<ObservableCollection<TreeNodeBase>> BuildHierarchyAsync()
        {
            var rootNodes = new ObservableCollection<TreeNodeBase>();

            try
            {
                Console.WriteLine("=== BUILDING LAZY HIERARCHY WITH DRIVER ID - LOADING DRIVER TYPES ===");

                var driverTypes = await GetDriverTypesAsync();
                Console.WriteLine($"Found {driverTypes.Count()} driver types");

                foreach (var driverType in driverTypes)
                {
                    var driverTypeNode = new DriverNode
                    {
                        Id = driverType.Id,
                        Name = driverType.Name,
                        DisplayName = $"🔌 {driverType.Name}",
                        DriverTypeName = driverType.Name,
                        IsExpanded = false,
                        IsChildrenLoaded = false
                    };

                    // Alt öğeleri olup olmadığını kontrol et
                    if (await HasDriversForDriverType(driverType.Id))
                    {
                        driverTypeNode.AddDummyChild();
                    }

                    rootNodes.Add(driverTypeNode);
                }

                Console.WriteLine($"Initial hierarchy built with {rootNodes.Count} driver types");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hierarchy build error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }

            return rootNodes;
        }

        /// <summary>
        /// Node genişletildiğinde çağrılacak method - Node tipine göre uygun yükleme metodunu çağırır
        /// </summary>
        public async Task LoadChildrenAsync(TreeNodeBase node)
        {
            if (node.IsChildrenLoaded || node.IsLoading)
                return;

            try
            {
                Console.WriteLine($"Loading children for {node.NodeType}: {node.Name}");
                node.IsLoading = true;
                node.RemoveDummyChild();

                switch (node)
                {
                    case DriverNode driverNode when !string.IsNullOrEmpty(driverNode.DriverTypeName):
                        // Bu bir DriverType node'u ise (DriverTypeName dolu)
                        if (driverNode.Children.Count == 0)
                        {
                            await LoadDriversForDriverType(driverNode, driverNode.Id);
                        }
                        break;

                    case DriverNode driverNode when string.IsNullOrEmpty(driverNode.DriverTypeName):
                        // Bu bir konkret Driver node'u ise - driver ID'ye göre channel'ları yükle
                        await LoadChannelsForDriver(driverNode, driverNode.Id);
                        break;

                    case ChannelNode channelNode:
                        // Channel node'u - o channel'daki device'ları yükle
                        await LoadDevicesForChannel(channelNode, channelNode.Id, channelNode.Name);
                        break;

                    case DeviceNode deviceNode:
                        await LoadTagsForDevice(deviceNode, deviceNode.Id, deviceNode.DeviceTypeId);
                        break;
                }

                node.IsChildrenLoaded = true;
                Console.WriteLine($"Successfully loaded {node.Children.Count} children for {node.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading children for {node.Name}: {ex.Message}");

                // Hata durumunda dummy child geri ekle
                if (node.Children.Count == 0)
                {
                    node.AddDummyChild();
                }
                throw;
            }
            finally
            {
                node.IsLoading = false;
            }
        }

        #endregion

        #region Data Loading Methods

        private async Task<IEnumerable<DriverType>> GetDriverTypesAsync()
        {
            const string sql = @"
                SELECT id, name, commonSettings
                FROM dbdataexchanger.drivertype
                ORDER BY name";

            return await _dbManager.QueryExchangerAsync<DriverType>(sql);
        }

        private async Task LoadDriversForDriverType(DriverNode driverTypeNode, int driverTypeId)
        {
            try
            {
                const string sql = @"
                    SELECT id, name, customSettings
                    FROM dbdataexchanger.driver
                    WHERE driverTypeId = @DriverTypeId
                    ORDER BY name";

                var drivers = await _dbManager.QueryExchangerAsync<Driver>(sql, new { DriverTypeId = driverTypeId });
                Console.WriteLine($"Loading {drivers.Count()} drivers for driver type {driverTypeNode.Name}");

                foreach (var driver in drivers)
                {
                    var driverNode = new DriverNode
                    {
                        Id = driver.Id,
                        Name = driver.Name,
                        DisplayName = $"🔧 {driver.Name}",
                        DriverTypeName = null, // Bu gerçek driver, DriverType değil
                        Parent = driverTypeNode,
                        IsExpanded = false,
                        IsChildrenLoaded = false
                    };

                    // Bu driver'ın channel'ları var mı kontrol et (driverId bazında)
                    if (await HasChannelsForDriver(driver.Id))
                    {
                        driverNode.AddDummyChild();
                    }

                    driverTypeNode.Children.Add(driverNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading drivers: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Driver ID'ye göre channel'ları yükle - channeldevice.driverId kullanarak
        /// </summary>
        private async Task LoadChannelsForDriver(DriverNode driverNode, int driverId)
        {
            try
            {
                const string sql = @"
                    SELECT DISTINCT cd.channelName, COUNT(*) as deviceCount, cd.driverId
                    FROM dbdataexchanger.channeldevice cd
                    WHERE cd.driverId = @DriverId
                    GROUP BY cd.channelName, cd.driverId
                    ORDER BY cd.channelName";

                var channels = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });
                Console.WriteLine($"Loading {channels.Count()} channels for driver {driverNode.Name}");

                foreach (var channel in channels)
                {
                    var channelNode = new ChannelNode
                    {
                        Id = driverId, // Driver ID'yi channel node'una atayalım
                        Name = channel.channelName ?? "Unknown Channel",
                        DisplayName = $"📡 {channel.channelName} ({channel.deviceCount} devices)",
                        ChannelTypeId = 0, // Artık channel type ID'ye ihtiyacımız yok
                        ChannelTypeName = null,
                        Parent = driverNode,
                        IsExpanded = false,
                        IsChildrenLoaded = false
                    };

                    // Bu channel'da device'lar var mı (driver ve channel name bazında)
                    if (await HasDevicesForDriverAndChannel(driverId, channel.channelName))
                    {
                        channelNode.AddDummyChild();
                    }

                    driverNode.Children.Add(channelNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading channels for driver: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Driver ID ve Channel Name'e göre device'ları yükle
        /// </summary>
        private async Task LoadDevicesForChannel(ChannelNode channelNode, int driverId, string channelName)
        {
            try
            {
                const string sql = @"
                    SELECT cd.id, cd.channelName, cd.deviceTypeId, cd.statusCode, 
                           cd.deviceJson, cd.updateTime, cd.driverId, dt.typeName, cds.statusDefinition
                    FROM dbdataexchanger.channeldevice cd
                    LEFT JOIN dbdataexchanger.devicetype dt ON cd.deviceTypeId = dt.id
                    LEFT JOIN dbdataexchanger.channeldevicestatus cds ON cd.statusCode = cds.statusCode
                    WHERE cd.driverId = @DriverId AND cd.channelName = @ChannelName
                    ORDER BY cd.id";

                var devices = await _dbManager.QueryExchangerAsync<dynamic>(sql,
                    new { DriverId = driverId, ChannelName = channelName });
                Console.WriteLine($"Loading {devices.Count()} devices for driver {driverId}, channel {channelName}");

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
                        IsExpanded = false,
                        IsChildrenLoaded = false
                    };

                    // Bu device'ın tag'leri var mı
                    if (await HasTagsForDevice((int)device.id, device.deviceTypeId ?? 0))
                    {
                        deviceNode.AddDummyChild();
                    }

                    channelNode.Children.Add(deviceNode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading devices: {ex.Message}");
                throw;
            }
        }

        private async Task LoadTagsForDevice(DeviceNode deviceNode, int deviceId, int deviceTypeId)
        {
            try
            {
                // Device Type Tags (genel tag'ler) - limitleyelim
                const string deviceTypeTagsSql = @"
                    SELECT id, tagJson
                    FROM dbdataexchanger.devicetypetag 
                    WHERE deviceTypeId = @DeviceTypeId
                    ORDER BY id
                    LIMIT 20"; // İlk 20 tag'i al

                var deviceTypeTags = await _dbManager.QueryExchangerAsync<DeviceTypeTag>(
                    deviceTypeTagsSql, new { DeviceTypeId = deviceTypeId });

                Console.WriteLine($"Loading {deviceTypeTags.Count()} tags for device {deviceId}");

                foreach (var tag in deviceTypeTags)
                {
                    var tagData = ParseTagJson(tag.TagJson);
                    if (tagData != null)
                    {
                        var tagValue = await GetCurrentTagValue(deviceId, tagData.Name);

                        var tagNode = new TagNode
                        {
                            Id = tag.Id,
                            Name = tagData.Name,
                            DisplayName = $"🏷️ {tagData.Name} [{tagData.DataType}] = {tagValue ?? "N/A"}",
                            TagAddress = tagData.Address,
                            DataType = tagData.DataType,
                            CurrentValue = tagValue,
                            Parent = deviceNode,
                            Quality = "Good",
                            IsWritable = tagData.IsWritable,
                            LastReadTime = DateTime.Now
                        };

                        deviceNode.Children.Add(tagNode);
                    }
                }

                // Individual Tags (cihaza özel tag'ler) de varsa ekle
                await LoadIndividualTagsForDevice(deviceNode, deviceId);

                Console.WriteLine($"Loaded total {deviceNode.Children.Count} tags for device {deviceId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading tags for device {deviceId}: {ex.Message}");
                throw;
            }
        }

        private async Task LoadIndividualTagsForDevice(DeviceNode deviceNode, int deviceId)
        {
            try
            {
                const string sql = @"
                    SELECT id, tagJson
                    FROM dbdataexchanger.deviceindividualtag 
                    WHERE channelDeviceId = @DeviceId
                    ORDER BY id
                    LIMIT 10"; // İlk 10 individual tag

                var individualTags = await _dbManager.QueryExchangerAsync<DeviceIndividualTag>(
                    sql, new { DeviceId = deviceId });

                foreach (var tag in individualTags)
                {
                    var tagData = ParseTagJson(tag.TagJson);
                    if (tagData != null)
                    {
                        var tagValue = await GetCurrentTagValue(deviceId, tagData.Name);

                        var tagNode = new TagNode
                        {
                            Id = tag.Id,
                            Name = tagData.Name,
                            DisplayName = $"🏷️ {tagData.Name} [Individual] [{tagData.DataType}] = {tagValue ?? "N/A"}",
                            TagAddress = tagData.Address,
                            DataType = tagData.DataType,
                            CurrentValue = tagValue,
                            Parent = deviceNode,
                            Quality = "Good",
                            IsWritable = tagData.IsWritable,
                            LastReadTime = DateTime.Now
                        };

                        deviceNode.Children.Add(tagNode);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading individual tags for device {deviceId}: {ex.Message}");
            }
        }

        #endregion

        #region Has Children Check Methods

        private async Task<bool> HasDriversForDriverType(int driverTypeId)
        {
            try
            {
                const string sql = @"
                    SELECT COUNT(*) 
                    FROM dbdataexchanger.driver 
                    WHERE driverTypeId = @DriverTypeId";

                var count = await _dbManager.QueryFirstExchangerAsync<int>(sql, new { DriverTypeId = driverTypeId });
                return count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Driver ID'ye göre channel var mı kontrol et
        /// </summary>
        private async Task<bool> HasChannelsForDriver(int driverId)
        {
            try
            {
                const string sql = @"
                    SELECT COUNT(DISTINCT channelName) 
                    FROM dbdataexchanger.channeldevice 
                    WHERE driverId = @DriverId";

                var count = await _dbManager.QueryFirstExchangerAsync<int>(sql, new { DriverId = driverId });
                return count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Driver ID ve Channel Name'e göre device var mı kontrol et
        /// </summary>
        private async Task<bool> HasDevicesForDriverAndChannel(int driverId, string channelName)
        {
            try
            {
                const string sql = @"
                    SELECT COUNT(*)
                    FROM dbdataexchanger.channeldevice
                    WHERE driverId = @DriverId AND channelName = @ChannelName";

                var count = await _dbManager.QueryFirstExchangerAsync<int>(sql,
                    new { DriverId = driverId, ChannelName = channelName });
                return count > 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> HasTagsForDevice(int deviceId, int deviceTypeId)
        {
            try
            {
                const string sql1 = @"
                    SELECT COUNT(*) 
                    FROM dbdataexchanger.devicetypetag 
                    WHERE deviceTypeId = @DeviceTypeId";

                const string sql2 = @"
                    SELECT COUNT(*) 
                    FROM dbdataexchanger.deviceindividualtag 
                    WHERE channelDeviceId = @DeviceId";

                var typeTagCount = await _dbManager.QueryFirstExchangerAsync<int>(sql1, new { DeviceTypeId = deviceTypeId });
                var individualTagCount = await _dbManager.QueryFirstExchangerAsync<int>(sql2, new { DeviceId = deviceId });

                return (typeTagCount + individualTagCount) > 0;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private async Task<object> GetCurrentTagValue(int deviceId, string tagName)
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

        private TagInfo ParseTagJson(string tagJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tagJson))
                    return null;

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
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing tag JSON: {ex.Message}");
                return null;
            }
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

        private string GetStatusDescription(byte statusCode)
        {
            return statusCode switch
            {
                11 => "Eklendi",
                31 => "Güncellendi",
                41 => "AktifEdildi",
                51 => "PasifEdildi",
                61 => "TagGüncellendi",
                0 => "Inactive",
                _ => $"Status_{statusCode}"
            };
        }

        private class TagInfo
        {
            public string Name { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public bool IsWritable { get; set; }
        }

        #endregion
    }
}