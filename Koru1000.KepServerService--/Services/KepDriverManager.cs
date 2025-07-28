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
    private readonly DatabaseManager.DatabaseManager _dbManager;
    private readonly ILogger<KepDriverManager> _logger;
    private readonly IKepRestApiManager _restApiManager;
    private readonly ConcurrentDictionary<int, KepClient> _clients = new();
    private readonly Timer _statusTimer;
    private readonly Timer _writeTimer;
    private readonly ConcurrentDictionary<int, DeviceOperationLock> _operationLocks = new();

    public DriverInfo DriverInfo => _driverInfo;

    public KepDriverManager(
        DriverInfo driverInfo,
        DatabaseManager.DatabaseManager dbManager,
        ILogger<KepDriverManager> logger,
        IKepRestApiManager restApiManager)
    {
        _driverInfo = driverInfo;
        _dbManager = dbManager;
        _logger = logger;
        _restApiManager = restApiManager;

        _statusTimer = new Timer(CheckStatus, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _writeTimer = new Timer(ProcessWriteOperations, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task StartAsync()
    {
        try
        {
            _logger.LogInformation($"🚀 Driver {_driverInfo.Name} (ID: {_driverInfo.Id}) başlatılıyor...");

            // 1. KEP Server'ı yeniden başlat (gerekirse)
            if (await ShouldRestartKepServerAsync())
            {
                await RestartKepServerServiceAsync();
            }

            // 2. Server konfigürasyonunu senkronize et
            await SyncServerConfigurationAsync();

            // 3. Client'ları başlat
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
                Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<KepClient>());

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
                WHERE id = @DeviceId AND driverId = @DriverId";

            return await _dbManager.QueryFirstOrDefaultExchangerAsync<int?>(sql,
                new { DeviceId = deviceId, DriverId = _driverInfo.Id });
        }
        catch
        {
            return null;
        }
    }

    // İŞLEM ÇAKIŞMASI ÇÖZÜMÜ - TÜM STATUSLAR İÇİN
    private async Task ProcessDeviceOperationAsync(int deviceId, byte newStatus)
    {
        var lockKey = deviceId;

        // Eğer bu device için zaten bir işlem devam ediyorsa, bekle
        if (_operationLocks.ContainsKey(lockKey))
        {
            _logger.LogWarning($"⚠️ Device {deviceId} için işlem zaten devam ediyor, bekleniyor...");
            return;
        }

        var operationLock = new DeviceOperationLock
        {
            DeviceId = deviceId,
            Status = newStatus,
            StartTime = DateTime.Now,
            IsProcessing = true
        };

        _operationLocks.TryAdd(lockKey, operationLock);

        try
        {
            _logger.LogInformation($"🔄 İşlem başlatılıyor: Device {deviceId}, Status {newStatus}");

            // Status'e göre işlem yap
            switch (newStatus)
            {
                case 11: // Ekleme
                    await AddDeviceToKepServerAsync(deviceId);
                    break;
                case 20: // Silme  
                case 21: // Silme
                    await RemoveDeviceFromKepServerAsync(deviceId);
                    break;
                case 31: // Güncelleme
                case 41:
                case 51:
                case 61:
                    await UpdateDeviceInKepServerAsync(deviceId);
                    break;
            }

            _logger.LogInformation($"✅ İşlem tamamlandı: Device {deviceId}, Status {newStatus}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"💥 İşlem başarısız: Device {deviceId}, Status {newStatus}");
        }
        finally
        {
            // Lock'u kaldır
            _operationLocks.TryRemove(lockKey, out _);
        }
    }

    private void OnClientDataChanged(object? sender, KepDataChangedEventArgs e)
    {
        // Veri değişikliklerini işle
    }

    private void OnClientStatusChanged(object? sender, KepStatusChangedEventArgs e)
    {
        // Status değişikliklerini işle
    }

    private async void CheckStatus(object? state)
    {
        // Periyodik status kontrolü
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

            foreach (var tag in tagsToWrite)
            {
                if (tag.clientId != null && _clients.TryGetValue((int)tag.clientId, out var client))
                {
                    var nodeId = $"ns={_driverInfo.CustomSettings.Namespace};s={tag.nodestring}";
                    await client.WriteTagAsync(nodeId, tag.tagValue);
                }
            }

            // Yazılan tag'leri sil
            if (tagsToWrite.Any())
            {
                const string deleteSql = @"
                    DELETE ty FROM kbindb._tagyaz ty
                    INNER JOIN dbdataexchanger.channeldevice cd ON cd.id = ty.devId
                    WHERE cd.driverId = @DriverId";

                await _dbManager.ExecuteKbinAsync(deleteSql, new { DriverId = _driverInfo.Id });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {_driverInfo.Name} tag yazma işlemi hatası");
        }
    }

    // Diğer helper metodlar...
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

            using var service = new ServiceController("KEPServerEX 6");
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
        // Server sync kodu buraya gelecek
    }

    private async Task AddDeviceToKepServerAsync(int deviceId)
    {
        // KEP Server'a device ekleme kodu
    }

    private async Task RemoveDeviceFromKepServerAsync(int deviceId)
    {
        // KEP Server'dan device silme kodu
    }

    private async Task UpdateDeviceInKepServerAsync(int deviceId)
    {
        // KEP Server'da device güncelleme kodu
    }
}

// İşlem lock sınıfı
public class DeviceOperationLock
{
    public int DeviceId { get; set; }
    public byte Status { get; set; }
    public DateTime StartTime { get; set; }
    public bool IsProcessing { get; set; }
}