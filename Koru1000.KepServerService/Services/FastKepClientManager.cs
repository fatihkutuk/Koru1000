// Koru1000.KepServerService/Services/FastKepClientManager.cs
using Koru1000.Core.Models.OpcModels;
using Koru1000.KepServerService.Clients;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Koru1000.KepServerService.Services
{
    public class FastKepClientManager
    {
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly OpcServiceConfig _config;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, FastKepClient> _clients;
        private readonly Timer _statusTimer;
        private bool _isRunning;

        public FastKepClientManager(DatabaseManager.DatabaseManager dbManager, OpcServiceConfig config, ILogger logger)
        {
            _dbManager = dbManager;
            _config = config;
            _logger = logger;
            _clients = new ConcurrentDictionary<int, FastKepClient>();
            _statusTimer = new Timer(CheckStatus, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation("🚀 FastKepClientManager başlatılıyor...");
                _isRunning = true;

                await _dbManager.ExecuteExchangerAsync("UPDATE channeldevice SET clientId = NULL");
                _logger.LogInformation("✅ Client states temizlendi");

                var clientList = await GetClientListAsync();
                _logger.LogInformation($"📋 {clientList.Count} client konfigürasyonu oluşturuldu");

                // ESKİ SİSTEMİNİZ GİBİ - SIRALI BAŞLATMA (PARALLEL DEĞİL)
                foreach (var clientConfig in clientList)
                {
                    try
                    {
                        var client = new FastKepClient(clientConfig, _dbManager, _logger);
                        _clients.TryAdd(clientConfig.ClientId, client);

                        // ESKİ SİSTEM GİBİ - TEK TEK BAŞLAT
                        await client.StartClientAsync();

                        _logger.LogInformation($"✅ Client {clientConfig.ClientId} başlatıldı");

                        // KISA BEKLEYİŞ - Security error'ı engellemek için
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Client {clientConfig.ClientId} başlatılamadı");
                    }
                }

                var successCount = _clients.Count;
                _logger.LogInformation($"🎯 {successCount}/{clientList.Count} client başarıyla başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ FastKepClientManager başlatılamadı");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation("🛑 FastKepClientManager durduruluyor...");
                _isRunning = false;

                _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

                var stopTasks = _clients.Values.Select(client => client.StopAsync());
                await Task.WhenAll(stopTasks);

                _clients.Clear();
                _logger.LogInformation("✅ Tüm client'lar durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ FastKepClientManager durdurulamadı");
            }
        }

        private async Task<List<ClientConfig>> GetClientListAsync()
        {
            try
            {
                await _dbManager.ExecuteExchangerAsync("UPDATE channeldevice SET clientId = NULL");

                // Driver'ları al
                const string driverSql = @"
                   SELECT DISTINCT d.id, d.name, d.customSettings
                   FROM driver d
                   INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                   WHERE dt.name = 'KEPSERVEREX'
                   ORDER BY d.id";

                var drivers = await _dbManager.QueryExchangerAsync<dynamic>(driverSql);
                var clientConfigs = new List<ClientConfig>();
                int globalClientId = 1;

                foreach (var driver in drivers)
                {
                    var driverSettings = ParseDriverSettings(driver.customSettings?.ToString());

                    // TOPLAM TAG SAYISINI AL
                    var totalTagCount = await GetDriverTagCountAsync((int)driver.id);

                    if (totalTagCount == 0)
                    {
                        _logger.LogWarning($"Driver {driver.name} için tag bulunamadı");
                        continue;
                    }

                    // JSON'DAN tagsPerClient AYARINI OKU
                    int tagsPerClient = driverSettings.TagsPerClient > 0 ? driverSettings.TagsPerClient : 5000;
                    int clientCount = (int)Math.Ceiling((double)totalTagCount / tagsPerClient);

                    _logger.LogInformation($"🎯 Driver {driver.name}: {totalTagCount} tag, {tagsPerClient} tag/client, {clientCount} client");

                    // Bu driver için client'ları oluştur
                    for (int i = 0; i < clientCount; i++)
                    {
                        var clientConfig = new ClientConfig
                        {
                            ClientId = globalClientId++,
                            DriverId = (int)driver.id,
                            DriverName = driver.name,
                            EndpointUrl = driverSettings.EndpointUrl,
                            Username = driverSettings.Username,
                            Password = driverSettings.Password,
                            TagStartIndex = i * tagsPerClient,
                            TagCount = Math.Min(tagsPerClient, totalTagCount - (i * tagsPerClient))
                        };

                        clientConfigs.Add(clientConfig);
                    }
                }

                _logger.LogInformation($"📋 Toplam {clientConfigs.Count} client konfigürasyonu oluşturuldu");
                return clientConfigs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client konfigürasyonları yüklenirken hata");
                return new List<ClientConfig>();
            }
        }

        private async Task<int> GetDriverTagCountAsync(int driverId)
        {
            const string sql = @"
               SELECT COUNT(*) as TagCount
               FROM (
                   SELECT dtt.id AS DeviceTagId
                   FROM channeldevice d
                   INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                   WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
                   
                   UNION ALL
                   
                   SELECT dit.id AS DeviceTagId
                   FROM channeldevice d
                   INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                   WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
               ) as allTags";

            return await _dbManager.QueryFirstExchangerAsync<int>(sql, new { DriverId = driverId });
        }

        private DriverSettings ParseDriverSettings(string customSettings)
        {
            var settings = new DriverSettings
            {
                EndpointUrl = "opc.tcp://localhost:49320",
                Username = "",
                Password = "",
                TagsPerClient = 5000 // DEFAULT
            };

            try
            {
                if (!string.IsNullOrEmpty(customSettings))
                {
                    var doc = JsonDocument.Parse(customSettings);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("EndpointUrl", out var url))
                        settings.EndpointUrl = url.GetString() ?? settings.EndpointUrl;

                    if (root.TryGetProperty("credentials", out var creds))
                    {
                        if (creds.TryGetProperty("username", out var user))
                            settings.Username = user.GetString() ?? "";
                        if (creds.TryGetProperty("password", out var pass))
                            settings.Password = pass.GetString() ?? "";
                    }

                    // YENİ - JSON'DAN tagsPerClient OKU
                    if (root.TryGetProperty("tagsPerClient", out var tagsPerClient))
                        settings.TagsPerClient = tagsPerClient.GetInt32();

                    _logger.LogInformation($"📄 Driver settings: EndpointUrl={settings.EndpointUrl}, TagsPerClient={settings.TagsPerClient}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Driver settings parse edilemedi");
            }

            return settings;
        }

        private void CheckStatus(object? state)
        {
            if (!_isRunning) return;

            foreach (var client in _clients.Values)
            {
                _ = Task.Run(client.CheckConnectionAsync);
            }
        }
    }

    // Yardımcı sınıflar
    public class ClientConfig
    {
        public int ClientId { get; set; }
        public int DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public string EndpointUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public int TagStartIndex { get; set; }
        public int TagCount { get; set; }
        public int DeviceCount { get; set; }
        public int AvgTagsPerDevice { get; set; }
    }

    public class DriverSettings
    {
        public string EndpointUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public int TagsPerClient { get; set; } = 5000;
    }
}