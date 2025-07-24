// Koru1000.KepServerService/Worker.cs (TAMAMI - DÜZELTME)
using Koru1000.Core.Models.OpcModels;
using Koru1000.KepServerService.Services;
using System.Text.Json;

namespace Koru1000.KepServerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ISharedQueueService _queueService;
        private readonly KepServerClientPool _clientPool;

        public Worker(
            ILogger<Worker> logger,
            DatabaseManager.DatabaseManager dbManager,
            ISharedQueueService queueService,
            KepServerClientPool clientPool)
        {
            _logger = logger;
            _dbManager = dbManager;
            _queueService = queueService;
            _clientPool = clientPool;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("🚀 KEPServerEX Service başlatılıyor...");

                // Database connection test
                if (await _dbManager.TestExchangerConnectionAsync())
                {
                    _logger.LogInformation("✅ Database bağlantısı kuruldu");
                }
                else
                {
                    throw new Exception("❌ Database bağlantısı kurulamadı!");
                }

                // Shared Queue Service'i başlat
                await _queueService.StartAsync();
                _logger.LogInformation("✅ Shared Queue Service başlatıldı");

                // KEPServerEX Driver'ları yükle ve Client Pool'u başlat
                await LoadAndStartKepServerDriversAsync();

                // Ana servis döngüsü
                while (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("📊 KEPServerEX Service çalışıyor: {Time}", DateTimeOffset.Now);

                    // Queue statistics
                    var queueStats = await _queueService.GetStatisticsAsync();
                    _logger.LogInformation("📈 Queue Stats - Received: {Received}, Processed: {Processed}, Queue: {QueueSize}, DB Connections: {DbConnections}",
                        queueStats.TotalReceived, queueStats.TotalProcessed, queueStats.QueueSize, queueStats.DatabaseConnections);

                    await Task.Delay(30000, stoppingToken); // 30 saniye bekle
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX Service hatası");
                throw;
            }
        }

        private async Task LoadAndStartKepServerDriversAsync()
        {
            try
            {
                const string sql = @"
                    SELECT DISTINCT d.id, d.name, d.customSettings, dt.name as driverTypeName
                    FROM driver d
                    INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                    INNER JOIN driver_channeltype_relation dcr ON d.id = dcr.driverId
                    WHERE dt.name = 'KEPSERVEREX'
                    ORDER BY d.id";

                var drivers = await _dbManager.QueryExchangerAsync<dynamic>(sql);
                var driverCount = drivers.Count();
                _logger.LogInformation("🔍 Bulunan KEPSERVEREX driver'ları: {DriverCount}", driverCount);

                foreach (var driver in drivers)
                {
                    // ✅ Dynamic cast düzeltmeleri
                    var driverId = (int)driver.id;
                    var driverName = (string)driver.name;

                    _logger.LogInformation("🔧 Driver ID: {DriverId}, Name: {DriverName}", driverId, driverName);

                    // Fast tag loading
                    _logger.LogInformation("⚡ Fast loading tags for driver {DriverId}", driverId);
                    var startTime = DateTime.Now;

                    var driverInfo = await ParseDriverInfoAsync(driver);
                    if (driverInfo != null)
                    {
                        var loadTime = DateTime.Now - startTime;
                        _logger.LogInformation("✅ Tags loaded in {LoadTime}ms", loadTime.TotalMilliseconds);

                        // Client Pool ile başlat (çoklu client)
                        await _clientPool.StartAsync(driverInfo);
                        _logger.LogInformation("🏊‍♂️ Driver Client Pool başlatıldı: {DriverName}", driverInfo.DriverName);
                    }
                }

                _logger.LogInformation("🎯 Tüm KEPServerEX driver'ları başlatıldı!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX driver'ları yüklenirken hata");
                throw;
            }
        }

        private async Task<OpcDriverInfo?> ParseDriverInfoAsync(dynamic driverData)
        {
            try
            {
                int driverId = (int)driverData.id;
                string driverName = (string)driverData.name;
                string? customSettingsJson = driverData.customSettings?.ToString();

                // Enhanced Driver Config Parsing - Senin JSON config'ine göre
                var driverConfig = ParseEnhancedDriverConfig(customSettingsJson);

                _logger.LogInformation("🔧 Driver Config: {DriverName} - Endpoint: {EndpointUrl}, Security: {SecurityMode}, MaxTags: {MaxTags}",
                    driverName, driverConfig.EndpointUrl, driverConfig.Security.Mode, driverConfig.ConnectionSettings.MaxTagsPerSubscription);

                var channelTypeIds = await GetDriverChannelTypeIdsAsync(driverId);

                var driverInfo = new OpcDriverInfo
                {
                    DriverId = driverId,
                    DriverName = driverName,
                    DriverType = "KEPSERVEREX",
                    EndpointUrl = driverConfig.EndpointUrl,
                    IsEnabled = true,
                    CustomSettings = new Dictionary<string, object>(), // Config'den dolu gelecek
                    ChannelTypeIds = channelTypeIds,
                    Namespace = driverConfig.Namespace,
                    ProtocolType = driverConfig.ProtocolType,
                    AddressFormat = driverConfig.AddressFormat,
                    ConnectionSettings = driverConfig.ConnectionSettings,
                    Security = driverConfig.Security,
                    Credentials = driverConfig.Credentials
                };

                return driverInfo;
            }
            catch (Exception ex)
            {
                var driverName = driverData.name?.ToString() ?? "Unknown";
                _logger.LogError(ex, "❌ Driver info parse edilemedi: {DriverName}", driverName);
                return null;
            }
        }

        // ✅ Gelişmiş Driver Config Parser - Senin JSON format'ına göre
        private EnhancedDriverConfig ParseEnhancedDriverConfig(string? customSettingsJson)
        {
            var config = new EnhancedDriverConfig();

            try
            {
                if (string.IsNullOrEmpty(customSettingsJson))
                {
                    _logger.LogWarning("⚠️ Custom settings boş, default ayarlar kullanılıyor");
                    return config;
                }

                using var jsonDoc = JsonDocument.Parse(customSettingsJson);
                var root = jsonDoc.RootElement;

                // EndpointUrl
                if (root.TryGetProperty("EndpointUrl", out var endpointUrlElement))
                    config.EndpointUrl = endpointUrlElement.GetString() ?? config.EndpointUrl;

                // Namespace
                if (root.TryGetProperty("namespace", out var nsElement))
                    config.Namespace = nsElement.GetString() ?? config.Namespace;

                // Protocol Type
                if (root.TryGetProperty("protocolType", out var protocolElement))
                    config.ProtocolType = protocolElement.GetString() ?? config.ProtocolType;

                // Address Format
                if (root.TryGetProperty("addressFormat", out var addressFormatElement))
                    config.AddressFormat = addressFormatElement.GetString() ?? config.AddressFormat;

                // Description
                if (root.TryGetProperty("description", out var descElement))
                    config.Description = descElement.GetString() ?? "";

                // Security Settings
                if (root.TryGetProperty("security", out var securityElement))
                {
                    if (securityElement.TryGetProperty("mode", out var modeElement))
                        config.Security.Mode = modeElement.GetString() ?? config.Security.Mode;

                    if (securityElement.TryGetProperty("policy", out var policyElement))
                        config.Security.Policy = policyElement.GetString() ?? config.Security.Policy;

                    if (securityElement.TryGetProperty("userTokenType", out var tokenTypeElement))
                        config.Security.UserTokenType = tokenTypeElement.GetString() ?? config.Security.UserTokenType;
                }

                // Credentials
                if (root.TryGetProperty("credentials", out var credentialsElement))
                {
                    if (credentialsElement.TryGetProperty("username", out var usernameElement))
                        config.Credentials.Username = usernameElement.GetString() ?? "";

                    if (credentialsElement.TryGetProperty("password", out var passwordElement))
                        config.Credentials.Password = passwordElement.GetString() ?? "";
                }

                // Connection Settings
                if (root.TryGetProperty("connectionSettings", out var connectionElement))
                {
                    if (connectionElement.TryGetProperty("updateRate", out var updateRateElement))
                        config.ConnectionSettings.UpdateRate = updateRateElement.GetInt32();

                    if (connectionElement.TryGetProperty("groupDeadband", out var deadbandElement))
                        config.ConnectionSettings.GroupDeadband = deadbandElement.GetInt32();

                    if (connectionElement.TryGetProperty("sessionTimeout", out var sessionTimeoutElement))
                        config.ConnectionSettings.SessionTimeout = sessionTimeoutElement.GetInt32();

                    if (connectionElement.TryGetProperty("publishingInterval", out var publishingIntervalElement))
                        config.ConnectionSettings.PublishingInterval = publishingIntervalElement.GetInt32();

                    if (connectionElement.TryGetProperty("maxTagsPerSubscription", out var maxTagsElement))
                        config.ConnectionSettings.MaxTagsPerSubscription = maxTagsElement.GetInt32();
                }

                _logger.LogInformation("✅ Enhanced driver config parsed successfully");
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Enhanced driver config parse edilemedi, default ayarlar kullanılıyor");
                return config;
            }
        }

        private async Task<List<int>> GetDriverChannelTypeIdsAsync(int driverId)
        {
            try
            {
                const string sql = @"
                    SELECT channelTypeId 
                    FROM driver_channeltype_relation 
                    WHERE driverId = @DriverId";

                var results = await _dbManager.QueryExchangerAsync<int>(sql, new { DriverId = driverId });
                return results.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Driver {DriverId} için channel type ID'leri alınamadı", driverId);
                return new List<int>();
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("🛑 KEPServerEX Service durduruluyor...");

                // Client Pool'u durdur
                _clientPool?.Dispose();
                _logger.LogInformation("✅ Client Pool durduruldu");

                // Queue Service'i durdur
                await _queueService.StopAsync();
                _logger.LogInformation("✅ Queue Service durduruldu");

                await base.StopAsync(cancellationToken);
                _logger.LogInformation("🏁 KEPServerEX Service tamamen durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Service durdurulurken hata");
            }
        }
    }

    // ✅ Enhanced Driver Configuration Model
    public class EnhancedDriverConfig
    {
        public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";
        public string Namespace { get; set; } = "2";
        public string ProtocolType { get; set; } = "OPC";
        public string AddressFormat { get; set; } = "ns={namespace};s={channelName}.{deviceName}.{tagName}";
        public string Description { get; set; } = "";
        public KepSecuritySettings Security { get; set; } = new();
        public KepCredentials Credentials { get; set; } = new();
        public KepConnectionSettings ConnectionSettings { get; set; } = new();
    }
}