using System.Collections.Concurrent;
using Koru1000.KepServerService.Models;
using Koru1000.KepServerService.Clients;
using Microsoft.Extensions.Logging;
using System.ServiceProcess;

namespace Koru1000.KepServerService.Services;

public interface IKepDriverManager
{
    Task StartAsync();
    Task StopAsync();
    DriverInfo DriverInfo { get; }
    Task<bool> WriteTagAsync(string channelName, string deviceName, string tagName, object value);
}

public class KepDriverManager : IKepDriverManager
{
    private readonly DriverInfo _driverInfo;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly ILogger<KepDriverManager> _logger;
    private readonly IKepRestApiManager _restApiManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceOperationManager _deviceOperationManager;
    private readonly ConcurrentDictionary<int, KepClient> _clients = new();
    private readonly Timer _statusTimer;
    private readonly Timer _writeTimer;

    public DriverInfo DriverInfo => _driverInfo;

    public KepDriverManager(
        DriverInfo driverInfo,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        ILogger<KepDriverManager> logger,
        IKepRestApiManager restApiManager,
        ILoggerFactory loggerFactory,
        IDeviceOperationManager deviceOperationManager)
    {
        _driverInfo = driverInfo;
        _dbManager = dbManager;
        _logger = logger;
        _restApiManager = restApiManager;
        _loggerFactory = loggerFactory;
        _deviceOperationManager = deviceOperationManager;

        _statusTimer = new Timer(CheckStatus, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _writeTimer = new Timer(ProcessWriteOperations, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task StartAsync()
    {
        try
        {
            _logger.LogInformation($"🚀 Driver {_driverInfo.Name} (ID: {_driverInfo.Id}) başlatılıyor...");

            // 1. KEP Server'ı yeniden başlat (sadece ilk driver için)
            if (await ShouldRestartKepServerAsync())
            {
                await RestartKepServerServiceAsync();
            }

            // 2. REST API Manager'ı bu driver için başlat
            if (!await _restApiManager.InitializeForDriverAsync(_driverInfo.Id))
            {
                throw new Exception("REST API Manager başlatılamadı");
            }

            // 3. Config API bağlantısını test et
            if (!await _restApiManager.TestConnectionAsync(_driverInfo.Id))
            {
                throw new Exception("KEP Server Config API'ye bağlanılamadı");
            }

            // 4. Server konfigürasyonunu senkronize et
            await SyncServerConfigurationAsync();

            // 5. Client'ların doğru dağıtımını kontrol et ve düzelt
            await FixClientDistributionAsync();

            // 6. Client'ları başlat
            await LoadClientsAsync();

            _logger.LogInformation($"✅ Driver {_driverInfo.Name} başarıyla başlatıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} başlatılamadı");
            throw;
        }
    }
    public async Task StopAsync()
    {
        try
        {
            _logger.LogInformation($"🛑 Driver {_driverInfo.Name} durduruluyor...");

            _statusTimer?.Dispose();
            _writeTimer?.Dispose();

            // Tüm client'ları durdur
            var tasks = _clients.Values.Select(c => c.StopAsync());
            await Task.WhenAll(tasks);
            _clients.Clear();

            _logger.LogInformation($"✅ Driver {_driverInfo.Name} durduruldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} durdurulamadı");
        }
    }

    public async Task<bool> WriteTagAsync(string channelName, string deviceName, string tagName, object value)
    {
        try
        {
            // Bu driver'a ait client'ı bul
            var deviceId = int.Parse(deviceName);
            var clientId = await GetClientIdForDeviceAsync(deviceId);

            if (clientId.HasValue && _clients.TryGetValue(clientId.Value, out var client))
            {
                var nodeId = $"ns={_driverInfo.CustomSettings.Namespace};s={channelName}.{deviceName}.{tagName}";
                return await client.WriteTagAsync(nodeId, value);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Tag yazma hatası: {channelName}.{deviceName}.{tagName}");
            return false;
        }
    }

    private async Task FixClientDistributionAsync()
    {
        try
        {
            _logger.LogInformation($"🔧 Driver {_driverInfo.Name} client dağıtımı kontrol ediliyor...");

            // Bu driver'ın maxTagsPerClient değerini al
            var maxTagsPerClient = _driverInfo.CustomSettings.ConnectionSettings?.MaxTagsPerClient ?? 30000;

            // Bu driver'a ait toplam tag sayısını hesapla
            const string tagCountSql = @"
                SELECT COUNT(*) as TotalTags
                FROM (
                    SELECT cd.id
                    FROM channeldevice cd 
                    INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = cd.deviceTypeId
                    WHERE cd.driverId = @DriverId AND cd.statusCode IN (11,31,41,51,61)
                    
                    UNION ALL
                    
                    SELECT cd.id
                    FROM channeldevice cd 
                    INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = cd.id
                    WHERE cd.driverId = @DriverId AND cd.statusCode IN (11,31,41,51,61)
                ) as AllTags";

            var totalTags = await _dbManager.QueryFirstExchangerAsync<int>(tagCountSql, new { DriverId = _driverInfo.Id });
            var requiredClients = Math.Max(1, (int)Math.Ceiling((double)totalTags / maxTagsPerClient));

            _logger.LogInformation($"📊 Driver {_driverInfo.Name}: {totalTags} tag, {requiredClients} client gerekli");

            // Client dağıtımını yeniden yap
            await RedistributeDevicesAsync(requiredClients, maxTagsPerClient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} client dağıtımı düzeltilemedi");
        }
    }

    private async Task RedistributeDevicesAsync(int clientCount, int maxTagsPerClient)
    {
        try
        {
            // Bu driver'a ait tüm aktif cihazları al
            const string deviceSql = @"
                SELECT cd.id as DeviceId,
                       COALESCE(
                           (SELECT COUNT(*) FROM devicetypetag dtt WHERE dtt.deviceTypeId = cd.deviceTypeId) +
                           (SELECT COUNT(*) FROM deviceindividualtag dit WHERE dit.channelDeviceId = cd.id),
                           0
                       ) as TagCount
                FROM channeldevice cd
                WHERE cd.driverId = @DriverId 
                  AND cd.statusCode IN (11,31,41,51,61)
                ORDER BY TagCount DESC";

            var devices = await _dbManager.QueryExchangerAsync<dynamic>(deviceSql, new { DriverId = _driverInfo.Id });

            var clientTagCounts = new int[clientCount];
            var assignments = new List<(int DeviceId, int ClientId)>();

            // Device'ları client'lara dağıt
            foreach (var device in devices)
            {
                var deviceId = (int)device.DeviceId;
                var tagCount = (int)device.TagCount;

                // En az tag'ı olan client'ı bul
                var targetClientIndex = Array.IndexOf(clientTagCounts, clientTagCounts.Min());
                var targetClientId = targetClientIndex + 1;

                if (clientTagCounts[targetClientIndex] + tagCount <= maxTagsPerClient)
                {
                    clientTagCounts[targetClientIndex] += tagCount;
                    assignments.Add((deviceId, targetClientId));
                }
                else
                {
                    _logger.LogWarning($"⚠️ Device {deviceId} ({tagCount} tag) hiçbir client'a sığmıyor!");
                    // Yine de en az dolu olan client'a ata
                    clientTagCounts[targetClientIndex] += tagCount;
                    assignments.Add((deviceId, targetClientId));
                }
            }

            // Veritabanını güncelle
            foreach (var (deviceId, clientId) in assignments)
            {
                const string updateSql = @"
                    UPDATE channeldevice 
                    SET clientId = @ClientId 
                    WHERE id = @DeviceId AND driverId = @DriverId";

                await _dbManager.ExecuteExchangerAsync(updateSql,
                    new { ClientId = clientId, DeviceId = deviceId, DriverId = _driverInfo.Id });
            }

            // Sonuçları logla
            for (int i = 0; i < clientCount; i++)
            {
                _logger.LogInformation($"📊 Client {i + 1}: {clientTagCounts[i]} tag");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} device redistribution hatası");
        }
    }

    private async Task LoadClientsAsync()
    {
        try
        {
            // Bu driver'a ait aktif client ID'leri al
            const string sql = @"
                SELECT cd.clientId 
                FROM channeldevice cd 
                WHERE cd.driverId = @DriverId 
                  AND cd.clientId IS NOT NULL 
                  AND cd.statusCode IN (11,31,41,51,61)
                GROUP BY cd.clientId 
                ORDER BY cd.clientId";

            var clientIds = await _dbManager.QueryExchangerAsync<int>(sql, new { DriverId = _driverInfo.Id });

            _logger.LogInformation($"Driver {_driverInfo.Name} için {clientIds.Count()} client yükleniyor...");

            foreach (var clientId in clientIds)
            {
                await CreateClientAsync(clientId);
                await Task.Delay(1000); // Client'lar arası delay
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} client'ları yüklenemedi");
        }
    }

    private async Task CreateClientAsync(int clientId)
    {
        try
        {
            _logger.LogInformation($"Driver {_driverInfo.Name} - Client {clientId} oluşturuluyor...");

            // Driver-specific config oluştur
            var clientConfig = new KepServiceConfig
            {
                EndpointUrl = _driverInfo.CustomSettings.EndpointUrl,
                Security = new SecurityConfig
                {
                    Username = _driverInfo.CustomSettings.Credentials?.Username ?? "",
                    Password = _driverInfo.CustomSettings.Credentials?.Password ?? "",
                    Mode = _driverInfo.CustomSettings.Security?.Mode ?? "None",
                    Policy = _driverInfo.CustomSettings.Security?.Policy ?? "None"
                },
                Limits = new LimitsConfig
                {
                    SessionTimeoutMs = _driverInfo.CustomSettings.ConnectionSettings?.SessionTimeout ?? 600000,
                    PublishingIntervalMs = _driverInfo.CustomSettings.ConnectionSettings?.PublishingInterval ?? 2000,
                    MaxTagsPerClient = _driverInfo.CustomSettings.ConnectionSettings?.MaxTagsPerClient ?? 30000
                }
            };

            var client = new KepClient(
                clientId,
                clientConfig,
                _dbManager,
                _driverInfo.Id, // Driver ID'yi geç
                _loggerFactory.CreateLogger<KepClient>());

            client.DataChanged += OnClientDataChanged;
            client.StatusChanged += OnClientStatusChanged;

            await client.StartAsync();

            _clients.TryAdd(clientId, client);
            _logger.LogInformation($"✅ Driver {_driverInfo.Name} - Client {clientId} başlatıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} - Client {clientId} oluşturulamadı");
        }
    }

    private async Task<int?> GetClientIdForDeviceAsync(int deviceId)
    {
        try
        {
            const string sql = @"
            SELECT clientId 
            FROM channeldevice 
            WHERE id = @DeviceId AND driverId = @DriverId 
            AND clientId IS NOT NULL";

            var clientIds = await _dbManager.QueryExchangerAsync<int?>(sql,
                new { DeviceId = deviceId, DriverId = _driverInfo.Id });

            return clientIds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} için clientId alınamadı");
            return null;
        }
    }

    private void OnClientDataChanged(object? sender, KepDataChangedEventArgs e)
    {
        // Veri değişikliklerini veritabanına kaydet
        _ = Task.Run(async () =>
        {
            try
            {
                const string sql = @"
                    INSERT INTO kbindb._tagoku (devId, tagName, tagValue, readTime)
                    VALUES (@DeviceId, @TagName, @Value, @Timestamp)
                    ON DUPLICATE KEY UPDATE 
                        tagValue = VALUES(tagValue), 
                        readTime = VALUES(readTime)";

                await _dbManager.ExecuteKbinAsync(sql, new
                {
                    DeviceId = e.DeviceId,
                    TagName = e.TagName,
                    Value = e.Value?.ToString() ?? "",
                    Timestamp = e.Timestamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Veri kaydetme hatası: {e.TagName}");
            }
        });
    }

    private void OnClientStatusChanged(object? sender, KepStatusChangedEventArgs e)
    {
        // Status değişikliklerini logla
        _logger.LogDebug($"📊 Client {e.ClientId} Status: {e.Status}, Messages: {e.TotalMessagesReceived}/{e.TotalMessagesProcessed}");
    }

    private async void CheckStatus(object? state)
    {
        try
        {
            var connectedClients = _clients.Count(c => c.Value != null);
            _logger.LogInformation($"📊 Driver {_driverInfo.Name}: {connectedClients}/{_clients.Count} clients connected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} status check hatası");
        }
    }

    private async void ProcessWriteOperations(object? state)
    {
        try
        {
            // Bu driver'a ait yazılacak tag'leri al
            const string sql = @"
                SELECT cd.id, CONCAT(cd.channelName,'.',cd.id,'.',ty.tagName) AS nodestring,
                       ty.tagValue, cd.driverId, cd.clientId, cd.channelName,
                       ty.tagName, ty.devId
                FROM kbindb._tagyaz ty
                INNER JOIN dbdataexchanger.channeldevice cd ON cd.id = ty.devId
                WHERE cd.driverId = @DriverId";

            var tagsToWrite = await _dbManager.QueryKbinAsync<dynamic>(sql, new { DriverId = _driverInfo.Id });

            if (!tagsToWrite.Any()) return;

            var groupedTags = tagsToWrite.GroupBy(t => (int)t.clientId);

            foreach (var clientGroup in groupedTags)
            {
                var clientId = clientGroup.Key;
                if (_clients.TryGetValue(clientId, out var client))
                {
                    foreach (var tag in clientGroup)
                    {
                        var nodeId = $"ns={_driverInfo.CustomSettings.Namespace};s={tag.nodestring}";
                        await client.WriteTagAsync(nodeId, tag.tagValue);
                        await Task.Delay(10); // Tag'lar arası küçük delay
                    }
                }
            }

            // Yazılan tag'leri sil
            const string deleteSql = @"
                DELETE ty FROM kbindb._tagyaz ty
                INNER JOIN dbdataexchanger.channeldevice cd ON cd.id = ty.devId
                WHERE cd.driverId = @DriverId";

            await _dbManager.ExecuteKbinAsync(deleteSql, new { DriverId = _driverInfo.Id });

            _logger.LogDebug($"✅ Driver {_driverInfo.Name}: {tagsToWrite.Count()} tag yazıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} tag yazma işlemi hatası");
        }
    }

    // Helper metodlar
    private async Task<bool> ShouldRestartKepServerAsync()
    {
        const string sql = @"
            SELECT MIN(d.id) 
            FROM driver d 
            INNER JOIN drivertype dt ON d.driverTypeId = dt.id 
            WHERE dt.name = 'KEPSERVEREX'";

        var firstDriverId = await _dbManager.QueryFirstExchangerAsync<int>(sql);
        return _driverInfo.Id == firstDriverId;
    }

    private async Task<bool> RestartKepServerServiceAsync()
    {
        try
        {
            _logger.LogInformation($"🔄 KEP Server servisi yeniden başlatılıyor...");

            using var service = new ServiceController("KEPServerEXV6");
            var timeout = TimeSpan.FromMinutes(2);

            if (service.Status == ServiceControllerStatus.Running)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, timeout);

            await Task.Delay(10000);

            _logger.LogInformation("✅ KEP Server servisi başlatıldı");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Server servisi başlatılamadı");
            return false;
        }
    }

    private async Task SyncServerConfigurationAsync()
    {
        try
        {
            _logger.LogInformation($"🔄 Driver {_driverInfo.Name} server konfigürasyonu senkronize ediliyor...");

            // Bu driver'a ait cihazları al ve KEP Server'a senkronize et
            const string deviceSql = @"
                SELECT cd.id, cd.channelName, cd.deviceJson, cd.statusCode, cd.clientId,
                       cd.channelJson, cd.deviceTypeId
                FROM channeldevice cd 
                WHERE cd.driverId = @DriverId 
                  AND cd.statusCode IN (11,31,41,51,61)
                ORDER BY cd.channelName, cd.id";

            var devices = await _dbManager.QueryExchangerAsync<dynamic>(deviceSql, new { DriverId = _driverInfo.Id });

            // Channel'ları grupla ve senkronize et
            var channelGroups = devices.GroupBy(d => d.channelName);

            foreach (var channelGroup in channelGroups)
            {
                await SyncChannelAsync(channelGroup.Key, channelGroup.ToList());
                await Task.Delay(100); // API rate limiting
            }

            _logger.LogInformation($"✅ Driver {_driverInfo.Name} konfigürasyonu senkronize edildi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} konfigürasyonu senkronize edilemedi");
        }
    }

    private async Task SyncChannelAsync(string channelName, List<dynamic> devices)
    {
        try
        {
            if (!devices.Any()) return;

            // Channel'ı oluştur/güncelle
            var firstDevice = devices.First();
            var channelJson = firstDevice.channelJson?.ToString();

            if (!string.IsNullOrEmpty(channelJson))
            {
                await _restApiManager.CreateOrUpdateChannelAsync(_driverInfo.Id, channelName, channelJson);
                await Task.Delay(200);
            }

            // Device'ları oluştur/güncelle
            foreach (var device in devices)
            {
                var deviceId = (int)device.id;
                var deviceJson = device.deviceJson?.ToString();

                if (!string.IsNullOrEmpty(deviceJson))
                {
                    await _restApiManager.CreateOrUpdateDeviceAsync(_driverInfo.Id, channelName, deviceId.ToString(), deviceJson);
                    await Task.Delay(100);

                    // Device'ın tag'larını da oluştur
                    await SyncDeviceTagsAsync(deviceId, channelName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Channel {channelName} senkronize edilemedi");
        }
    }

    private async Task SyncDeviceTagsAsync(int deviceId, string channelName)
    {
        try
        {
            // Device'ın tüm tag'larını al
            const string tagSql = @"
                SELECT JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                       dtt.tagJson
                FROM channeldevice cd 
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = cd.deviceTypeId
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId
                
                UNION ALL
                
                SELECT JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                       dit.tagJson
                FROM channeldevice cd 
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = cd.id
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId";

            var tags = await _dbManager.QueryExchangerAsync<dynamic>(tagSql,
                new { DeviceId = deviceId, DriverId = _driverInfo.Id });

            foreach (var tag in tags)
            {
                if (!string.IsNullOrEmpty(tag.tagJson?.ToString()) && !string.IsNullOrEmpty(tag.TagName))
                {
                    await _restApiManager.CreateOrUpdateTagAsync(_driverInfo.Id, channelName, deviceId.ToString(), tag.TagName, tag.tagJson.ToString());
                    await Task.Delay(50); // Tag'lar arası delay
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} tag'ları senkronize edilemedi");
        }
    }
}