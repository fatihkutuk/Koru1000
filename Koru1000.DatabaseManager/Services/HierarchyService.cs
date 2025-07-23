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
        /// Driver ID ve Channel Name'e göre device'ları yükle - Channel Type bilgisini de al
        /// </summary>
        /// <summary>
        /// Driver ID ve Channel Name'e göre device'ları yükle - Driver Settings'i de al
        /// </summary>
        private async Task LoadDevicesForChannel(ChannelNode channelNode, int driverId, string channelName)
        {
            try
            {
                const string sql = @"
            SELECT cd.id, cd.channelName, cd.deviceTypeId, cd.statusCode, 
                   cd.deviceJson, cd.updateTime, cd.driverId, dt.typeName, 
                   cds.statusDefinition, ct.name as channelTypeName,
                   d.customSettings as driverCustomSettings, d.name as driverName
            FROM dbdataexchanger.channeldevice cd
            LEFT JOIN dbdataexchanger.devicetype dt ON cd.deviceTypeId = dt.id
            LEFT JOIN dbdataexchanger.channeltypes ct ON dt.ChannelTypeId = ct.id
            LEFT JOIN dbdataexchanger.channeldevicestatus cds ON cd.statusCode = cds.statusCode
            LEFT JOIN dbdataexchanger.driver d ON cd.driverId = d.id
            WHERE cd.driverId = @DriverId AND cd.channelName = @ChannelName
            ORDER BY cd.id";

                var devices = await _dbManager.QueryExchangerAsync<dynamic>(sql,
                    new { DriverId = driverId, ChannelName = channelName });
                Console.WriteLine($"Loading {devices.Count()} devices for driver {driverId}, channel {channelName}");

                // Driver settings'ini parse et
                DriverSettings driverSettings = null;
                if (devices.Any())
                {
                    var firstDevice = devices.First();
                    driverSettings = ParseDriverCustomSettings(firstDevice.driverCustomSettings?.ToString());
                }

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
                        IsChildrenLoaded = false,
                        // Custom properties
                        ChannelTypeName = device.channelTypeName ?? "Unknown",
                        DriverSettings = driverSettings
                    };

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

        private DriverSettings ParseDriverCustomSettings(string customSettingsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(customSettingsJson))
                    return new DriverSettings { ProtocolType = "UNKNOWN" };

                using var doc = JsonDocument.Parse(customSettingsJson);
                var root = doc.RootElement;

                var settings = new DriverSettings
                {
                    ProtocolType = root.TryGetProperty("protocolType", out var protocolType) ?
                                  protocolType.GetString() : "UNKNOWN"
                };

                // Protocol-specific parsing
                switch (settings.ProtocolType?.ToUpper())
                {
                    case "OPC":
                        settings.Namespace = root.TryGetProperty("namespace", out var ns) ?
                                           ns.GetString() : "0";
                        settings.AddressFormat = root.TryGetProperty("addressFormat", out var addrFormat) ?
                                                addrFormat.GetString() : "ns={namespace};{channelName}.{deviceName}.{tagName}";
                        break;

                    case "MODBUS":
                        if (root.TryGetProperty("addressFormat", out var modbusAddrFormat))
                        {
                            if (modbusAddrFormat.TryGetProperty("showFunctionCode", out var showFC))
                                settings.AddressFormatSettings["showFunctionCode"] = showFC.GetBoolean();
                            if (modbusAddrFormat.TryGetProperty("showDataType", out var showDT))
                                settings.AddressFormatSettings["showDataType"] = showDT.GetBoolean();
                            if (modbusAddrFormat.TryGetProperty("format", out var format))
                                settings.AddressFormat = format.GetString();
                        }
                        break;

                    case "MQTT":
                        if (root.TryGetProperty("addressFormat", out var mqttAddrFormat))
                        {
                            if (mqttAddrFormat.TryGetProperty("topicFormat", out var topicFormat))
                                settings.AddressFormat = topicFormat.GetString();
                            if (mqttAddrFormat.TryGetProperty("payloadType", out var payloadType))
                                settings.AddressFormatSettings["payloadType"] = payloadType.GetString();
                            if (mqttAddrFormat.TryGetProperty("jsonPath", out var jsonPath))
                                settings.AddressFormatSettings["jsonPath"] = jsonPath.GetString();
                        }
                        break;
                }

                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing driver custom settings: {ex.Message}");
                return new DriverSettings { ProtocolType = "UNKNOWN" };
            }
        }

        private async Task LoadTagsForDevice(DeviceNode deviceNode, int deviceId, int deviceTypeId)
        {
            try
            {
                const string deviceTypeTagsSql = @"
            SELECT id, tagJson
            FROM dbdataexchanger.devicetypetag 
            WHERE deviceTypeId = @DeviceTypeId
            ORDER BY id
            LIMIT 20";

                var deviceTypeTags = await _dbManager.QueryExchangerAsync<DeviceTypeTag>(
                    deviceTypeTagsSql, new { DeviceTypeId = deviceTypeId });

                Console.WriteLine($"Loading {deviceTypeTags.Count()} tags for device {deviceId}, protocol: {((DriverSettings)deviceNode.DriverSettings)?.ProtocolType}");

                foreach (var tag in deviceTypeTags)
                {
                    var tagAddressInfo = ParseTagForProtocol(tag.TagJson, deviceNode);
                    if (tagAddressInfo != null)
                    {
                        var tagValue = await GetCurrentTagValue(deviceId, tagAddressInfo.Name);

                        var tagNode = new TagNode
                        {
                            Id = tag.Id,
                            Name = tagAddressInfo.Name,
                            DisplayName = GetTagDisplayName(tagAddressInfo, tagValue),
                            TagAddress = tagAddressInfo.FormattedAddress,
                            DataType = tagAddressInfo.DataType,
                            CurrentValue = tagValue,
                            Parent = deviceNode,
                            Quality = "Good",
                            IsWritable = tagAddressInfo.IsWritable,
                            LastReadTime = DateTime.Now
                        };

                        deviceNode.Children.Add(tagNode);
                    }
                }

                await LoadIndividualTagsForDevice(deviceNode, deviceId);

                Console.WriteLine($"Loaded total {deviceNode.Children.Count} tags for device {deviceId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading tags for device {deviceId}: {ex.Message}");
                throw;
            }
        }

        private TagAddressInfo ParseTagForProtocol(string tagJson, DeviceNode deviceNode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tagJson))
                    return null;

                using var doc = JsonDocument.Parse(tagJson);
                var root = doc.RootElement;

                var driverSettings = (DriverSettings)deviceNode.DriverSettings;
                var protocolType = driverSettings?.ProtocolType?.ToUpper() ?? "UNKNOWN";

                var tagInfo = new TagAddressInfo
                {
                    Name = root.TryGetProperty("common.ALLTYPES_NAME", out var name) ?
                           name.GetString() : "Unknown",
                    Description = root.TryGetProperty("common.ALLTYPES_DESCRIPTION", out var desc) ?
                                 desc.GetString() : "",
                    DataType = root.TryGetProperty("servermain.TAG_DATA_TYPE", out var type) ?
                              GetDataTypeName(type.GetInt32()) : "Unknown",
                    IsWritable = root.TryGetProperty("servermain.TAG_READ_WRITE_ACCESS", out var access) ?
                                access.GetInt32() != 0 : false,
                    Address = root.TryGetProperty("servermain.TAG_ADDRESS", out var addr) ?
                             addr.GetString() : "0",
                    ScanRateMs = root.TryGetProperty("servermain.TAG_SCAN_RATE_MILLISECONDS", out var scanRate) ?
                                scanRate.GetInt32() : 1000,
                    ProtocolType = protocolType
                };

                // Scaling bilgilerini al
                ParseScalingInfo(root, tagInfo);

                // Protocol-specific address formatting
                switch (protocolType)
                {
                    case "OPC":
                        tagInfo.FormattedAddress = BuildOpcAddress(root, tagInfo, deviceNode, driverSettings);
                        tagInfo.DisplayAddress = $"Node: {tagInfo.FormattedAddress}";
                        break;

                    case "MODBUS":
                        tagInfo.FormattedAddress = BuildModbusAddress(root, tagInfo, deviceNode, driverSettings);
                        tagInfo.DisplayAddress = tagInfo.FormattedAddress;
                        break;

                    case "MQTT":
                        tagInfo.FormattedAddress = BuildMqttAddress(root, tagInfo, deviceNode, driverSettings);
                        tagInfo.DisplayAddress = $"Topic: {tagInfo.FormattedAddress}";
                        break;

                    case "DNP3":
                        tagInfo.FormattedAddress = BuildDnp3Address(root, tagInfo, deviceNode, driverSettings);
                        tagInfo.DisplayAddress = tagInfo.FormattedAddress;
                        break;

                    default:
                        // Fallback - direkt TAG_ADDRESS kullan
                        tagInfo.FormattedAddress = tagInfo.Address;
                        tagInfo.DisplayAddress = $"Addr: {tagInfo.Address}";
                        break;
                }

                return tagInfo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing tag for protocol: {ex.Message}");
                return null;
            }
        }

        private void ParseScalingInfo(JsonElement root, TagAddressInfo tagInfo)
        {
            try
            {
                tagInfo.HasScaling = root.TryGetProperty("servermain.TAG_SCALING_TYPE", out var scalingType) &&
                                    scalingType.GetInt32() != 0;

                if (tagInfo.HasScaling)
                {
                    tagInfo.RawLow = root.TryGetProperty("servermain.TAG_SCALING_RAW_LOW", out var rawLow) ?
                                    rawLow.GetDouble() : 0;
                    tagInfo.RawHigh = root.TryGetProperty("servermain.TAG_SCALING_RAW_HIGH", out var rawHigh) ?
                                     rawHigh.GetDouble() : 1000;
                    tagInfo.ScaledLow = root.TryGetProperty("servermain.TAG_SCALING_SCALED_LOW", out var scaledLow) ?
                                       scaledLow.GetDouble() : 0;
                    tagInfo.ScaledHigh = root.TryGetProperty("servermain.TAG_SCALING_SCALED_HIGH", out var scaledHigh) ?
                                        scaledHigh.GetDouble() : 1000;
                    tagInfo.ScalingUnits = root.TryGetProperty("servermain.TAG_SCALING_UNITS", out var units) ?
                                          units.GetString() : "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing scaling info: {ex.Message}");
                tagInfo.HasScaling = false;
            }
        }

        private string BuildOpcAddress(JsonElement root, TagAddressInfo tagInfo, DeviceNode deviceNode, DriverSettings driverSettings)
        {
            // Önce direkt node string var mı kontrol et (KEPServer gibi durumlarda)
            if (root.TryGetProperty("opc.NODE_STRING", out var nodeString) && !string.IsNullOrWhiteSpace(nodeString.GetString()))
            {
                return nodeString.GetString();
            }

            // Node string yoksa, driver settings'den namespace ile oluştur
            var namespaceName = driverSettings?.Namespace ?? "0";
            var channelName = deviceNode.Parent?.Name ?? "UnknownChannel";
            var deviceName = deviceNode.Name ?? "UnknownDevice";

            // Driver settings'de format varsa kullan
            if (!string.IsNullOrWhiteSpace(driverSettings?.AddressFormat))
            {
                return driverSettings.AddressFormat
                    .Replace("{namespace}", namespaceName)
                    .Replace("{channelName}", channelName)
                    .Replace("{deviceName}", deviceName)
                    .Replace("{tagName}", tagInfo.Name);
            }

            // Varsayılan OPC format
            return $"ns={namespaceName};{channelName}.{deviceName}.{tagInfo.Name}";
        }

        private string BuildModbusAddress(JsonElement root, TagAddressInfo tagInfo, DeviceNode deviceNode, DriverSettings driverSettings)
        {
            // servermain.TAG_ADDRESS'den gerçek adresi al
            var address = tagInfo.Address; // Bu zaten servermain.TAG_ADDRESS'den alınmış

            // Function Code'u tahmin et (data type'a göre)
            var functionCode = EstimateModbusFunctionCode(root, tagInfo);

            var showFunctionCode = driverSettings?.AddressFormatSettings.ContainsKey("showFunctionCode") == true ?
                                  (bool)driverSettings.AddressFormatSettings["showFunctionCode"] : true;
            var showDataType = driverSettings?.AddressFormatSettings.ContainsKey("showDataType") == true ?
                              (bool)driverSettings.AddressFormatSettings["showDataType"] : true;
            var showScanRate = driverSettings?.AddressFormatSettings.ContainsKey("showScanRate") == true ?
                              (bool)driverSettings.AddressFormatSettings["showScanRate"] : false;

            var addressParts = new List<string> { $"Addr: {address}" };

            if (showFunctionCode)
                addressParts.Add($"FC: {functionCode}");

            if (showDataType)
                addressParts.Add($"Type: {tagInfo.DataType}");

            if (showScanRate && tagInfo.ScanRateMs > 0)
                addressParts.Add($"Scan: {tagInfo.ScanRateMs}ms");

            // Scaling bilgisi varsa ekle
            if (tagInfo.HasScaling)
            {
                var scalingInfo = $"Scale: {tagInfo.RawLow}-{tagInfo.RawHigh} → {tagInfo.ScaledLow}-{tagInfo.ScaledHigh}";
                if (!string.IsNullOrWhiteSpace(tagInfo.ScalingUnits))
                    scalingInfo += $" {tagInfo.ScalingUnits}";
                addressParts.Add(scalingInfo);
            }

            return string.Join(", ", addressParts);
        }

        private int EstimateModbusFunctionCode(JsonElement root, TagAddressInfo tagInfo)
        {
            // Tag'ın read/write durumuna ve data type'ına göre function code tahmin et
            var isWritable = tagInfo.IsWritable;
            var address = int.TryParse(tagInfo.Address, out var addr) ? addr : 0;

            // Genel Modbus function code kuralları
            if (address >= 1 && address <= 9999) // Coils
                return isWritable ? 5 : 1; // Write Single Coil : Read Coils
            else if (address >= 10001 && address <= 19999) // Discrete Inputs
                return 2; // Read Discrete Inputs
            else if (address >= 30001 && address <= 39999) // Input Registers
                return 4; // Read Input Registers
            else if (address >= 40001 && address <= 49999) // Holding Registers
                return isWritable ? 6 : 3; // Write Single Register : Read Holding Registers
            else
                return 3; // Default: Read Holding Registers
        }

        private string BuildMqttAddress(JsonElement root, TagAddressInfo tagInfo, DeviceNode deviceNode, DriverSettings driverSettings)
        {
            // MQTT için önce alias var mı kontrol et
            if (root.TryGetProperty("mqtt.ALIAS", out var alias) && !string.IsNullOrWhiteSpace(alias.GetString()))
            {
                return alias.GetString();
            }

            // Topic format kullanarak oluştur
            var topicFormat = driverSettings?.AddressFormat ?? "{channelName}/{deviceName}/{tagName}";
            var channelName = deviceNode.Parent?.Name ?? "unknown";
            var deviceName = deviceNode.Name ?? "unknown";

            var topic = topicFormat
                .Replace("{channelName}", channelName)
                .Replace("{deviceName}", deviceName)
                .Replace("{tagName}", tagInfo.Name);

            // Payload type bilgisi varsa ekle
            var payloadType = driverSettings?.AddressFormatSettings.ContainsKey("payloadType") == true ?
                             (string)driverSettings.AddressFormatSettings["payloadType"] : "JSON";

            if (payloadType != "RAW")
            {
                var jsonPath = driverSettings?.AddressFormatSettings.ContainsKey("jsonPath") == true ?
                              (string)driverSettings.AddressFormatSettings["jsonPath"] : "$.value";
                return $"{topic} ({payloadType}: {jsonPath})";
            }

            return topic;
        }

        private string BuildDnp3Address(JsonElement root, TagAddressInfo tagInfo, DeviceNode deviceNode, DriverSettings driverSettings)
        {
            var address = tagInfo.Address;
            var dataType = tagInfo.DataType;

            // DNP3 için point type'ı belirle
            var pointType = "AI"; // Analog Input default
            if (tagInfo.DataType.Contains("Boolean") || tagInfo.DataType.Contains("Word"))
                pointType = tagInfo.IsWritable ? "BO" : "BI"; // Binary Output/Input
            else if (tagInfo.IsWritable)
                pointType = "AO"; // Analog Output

            var addressParts = new List<string>
    {
        $"Point: {pointType}{address}",
        $"Type: {dataType}"
    };

            if (tagInfo.ScanRateMs > 0)
                addressParts.Add($"Scan: {tagInfo.ScanRateMs}ms");

            return string.Join(", ", addressParts);
        }

        private string GetTagDisplayName(TagAddressInfo tagInfo, object tagValue, bool isIndividual = false)
        {
            var individualText = isIndividual ? " [Individual]" : "";
            var protocolIcon = GetProtocolIcon(tagInfo.ProtocolType);

            return $"🏷️ {tagInfo.Name}{individualText} [{tagInfo.DataType}] {protocolIcon} = {tagValue ?? "N/A"}";
        }

        private string GetProtocolIcon(string protocolType)
        {
            return protocolType?.ToUpper() switch
            {
                "OPC" => "🔗",
                "MODBUS" => "📡",
                "MQTT" => "📨",
                "DNP3" => "⚡",
                _ => "❓"
            };
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
                    var tagData = ParseTagJson(tag.TagJson, deviceNode.ChannelTypeName);
                    if (tagData != null)
                    {
                        var tagValue = await GetCurrentTagValue(deviceId, tagData.Name);

                        var tagNode = new TagNode
                        {
                            Id = tag.Id,
                            Name = tagData.Name,
                            DisplayName = GetTagDisplayName(tagData, tagValue, deviceNode.ChannelTypeName, true),
                            TagAddress = GetTagAddress(tagData, deviceNode.ChannelTypeName),
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

        private TagInfo ParseTagJson(string tagJson, string channelTypeName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tagJson))
                    return null;

                using var doc = JsonDocument.Parse(tagJson);
                var root = doc.RootElement;

                var tagInfo = new TagInfo
                {
                    Name = root.TryGetProperty("common.ALLTYPES_NAME", out var name) ?
                           name.GetString() : "Unknown",
                    DataType = root.TryGetProperty("servermain.TAG_DATA_TYPE", out var type) ?
                              GetDataTypeName(type.GetInt32()) : "Unknown",
                    IsWritable = root.TryGetProperty("servermain.TAG_READ_WRITE_ACCESS", out var access) ?
                                access.GetInt32() != 0 : false,
                    ChannelTypeName = channelTypeName
                };

                // Channel Type'a göre adres bilgilerini parse et
                switch (channelTypeName?.ToUpper())
                {
                    case "OPC" or "OPC-UA":
                        // OPC için node bilgilerini al
                        tagInfo.OpcNodeId = root.TryGetProperty("opc.NODE_ID", out var nodeId) ?
                                           nodeId.GetString() : null;
                        tagInfo.OpcNamespace = root.TryGetProperty("opc.NAMESPACE", out var ns) ?
                                              ns.GetString() : null;
                        tagInfo.Address = $"ns={tagInfo.OpcNamespace};i={tagInfo.OpcNodeId}";
                        break;

                    case "KEPSERVER" or "KEPSERVEREX":
                        // KEPServer için node string'i al
                        tagInfo.NodeString = root.TryGetProperty("kep.NODE_STRING", out var nodeString) ?
                                            nodeString.GetString() : null;
                        tagInfo.Address = tagInfo.NodeString ??
                                         (root.TryGetProperty("servermain.TAG_ADDRESS", out var addr) ?
                                          addr.GetString() : "");
                        break;

                    case "MODBUS" or "MODBUS TCP IP":
                        // Modbus için adres ve function code'u al
                        tagInfo.ModbusAddress = root.TryGetProperty("modbus.ADDRESS", out var mbAddr) ?
                                               mbAddr.GetString() : null;
                        tagInfo.ModbusFunctionCode = root.TryGetProperty("modbus.FUNCTION_CODE", out var funcCode) ?
                                                    funcCode.GetInt32() : null;
                        tagInfo.Address = $"{tagInfo.ModbusAddress}" +
                                         (tagInfo.ModbusFunctionCode.HasValue ? $" (FC:{tagInfo.ModbusFunctionCode})" : "");
                        break;

                    default:
                        // Genel durumda TAG_ADDRESS'i kullan
                        tagInfo.Address = root.TryGetProperty("servermain.TAG_ADDRESS", out var defaultAddr) ?
                                         defaultAddr.GetString() : "";
                        break;
                }

                return tagInfo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing tag JSON: {ex.Message}");
                return null;
            }
        }
        private string GetTagDisplayName(TagInfo tagData, object tagValue, string channelTypeName, bool isIndividual = false)
        {
            var individualText = isIndividual ? " [Individual]" : "";
            var channelTypeIcon = GetChannelTypeIcon(channelTypeName);

            return $"🏷️ {tagData.Name}{individualText} [{tagData.DataType}] {channelTypeIcon} = {tagValue ?? "N/A"}";
        }

        private string GetTagAddress(TagInfo tagData, string channelTypeName)
        {
            return channelTypeName?.ToUpper() switch
            {
                "OPC" or "OPC-UA" => $"Node: {tagData.OpcNodeId}, NS: {tagData.OpcNamespace}",
                "KEPSERVER" or "KEPSERVEREX" => $"Node: {tagData.NodeString}",
                "MODBUS" or "MODBUS TCP IP" => $"Addr: {tagData.ModbusAddress}" +
                                              (tagData.ModbusFunctionCode.HasValue ? $", FC: {tagData.ModbusFunctionCode}" : ""),
                _ => tagData.Address
            };
        }

        private string GetChannelTypeIcon(string channelTypeName)
        {
            return channelTypeName?.ToUpper() switch
            {
                "OPC" or "OPC-UA" => "🔗",
                "KEPSERVER" or "KEPSERVEREX" => "🔧",
                "MODBUS" or "MODBUS TCP IP" => "📡",
                _ => "❓"
            };
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

            // Channel type'a göre adres bilgileri
            public string OpcNodeId { get; set; }
            public string OpcNamespace { get; set; }
            public string NodeString { get; set; }
            public string ModbusAddress { get; set; }
            public int? ModbusFunctionCode { get; set; }
            public string ChannelTypeName { get; set; }
        }
        private class TagAddressInfo
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public bool IsWritable { get; set; }
            public string ProtocolType { get; set; }

            // Temel tag bilgileri
            public string Address { get; set; }
            public int ScanRateMs { get; set; }
            public string Description { get; set; }

            // Scaling bilgileri
            public bool HasScaling { get; set; }
            public double RawLow { get; set; }
            public double RawHigh { get; set; }
            public double ScaledLow { get; set; }
            public double ScaledHigh { get; set; }
            public string ScalingUnits { get; set; }

            // Protocol-specific data
            public Dictionary<string, object> ProtocolSpecificData { get; set; } = new();

            // Display için hesaplanmış değerler
            public string FormattedAddress { get; set; }
            public string DisplayAddress { get; set; }
        }

        private class DriverSettings
        {
            public string ProtocolType { get; set; }
            public string Namespace { get; set; }
            public string AddressFormat { get; set; }
            public Dictionary<string, object> ConnectionSettings { get; set; } = new();
            public Dictionary<string, object> AddressFormatSettings { get; set; } = new();
        }
        #endregion
    }
}