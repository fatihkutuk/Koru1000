using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services;

public interface IDeviceOperationManager
{
    Task StartAsync();
    Task StopAsync();
    Task ProcessDeviceOperationAsync(int deviceId, byte statusCode, int driverId);
}

public class DeviceOperationManager : IDeviceOperationManager
{
    private readonly ILogger<DeviceOperationManager> _logger;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly IKepRestApiManager _restApiManager;
    private readonly ConcurrentDictionary<int, DeviceOperationLock> _operationLocks = new();
    private Timer? _operationTimer;

    public DeviceOperationManager(
        ILogger<DeviceOperationManager> logger,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        IKepRestApiManager restApiManager)
    {
        _logger = logger;
        _dbManager = dbManager;
        _restApiManager = restApiManager;
    }

    public async Task StartAsync()
    {
        try
        {
            _logger.LogInformation("🚀 Device Operation Manager başlatılıyor...");

            _operationTimer = new Timer(ProcessAllPendingOperations, null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

            _logger.LogInformation("✅ Device Operation Manager başlatıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device Operation Manager başlatılamadı");
            throw;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _logger.LogInformation("🛑 Device Operation Manager durduruluyor...");

            _operationTimer?.Dispose();
            _operationTimer = null;

            _logger.LogInformation("✅ Device Operation Manager durduruldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device Operation Manager durdurulamadı");
        }
    }

    public async Task ProcessDeviceOperationAsync(int deviceId, byte statusCode, int driverId)
    {
        var operationLock = new DeviceOperationLock
        {
            DeviceId = deviceId,
            Status = statusCode,
            StartTime = DateTime.Now,
            IsProcessing = true
        };

        if (!_operationLocks.TryAdd(deviceId, operationLock))
        {
            _logger.LogWarning($"⚠️ Device {deviceId} için işlem zaten devam ediyor");
            return;
        }

        try
        {
            _logger.LogInformation($"🔄 İşlem başlatılıyor: Device {deviceId}, Status {statusCode}, Driver {driverId}");

            // Status'e göre işlem yap
            switch (statusCode)
            {
                case 11: // Ekleme
                    await AddDeviceToKepServerAsync(deviceId, driverId);
                    break;
                case 20: // Silme  
                case 21: // Silme
                    await RemoveDeviceFromKepServerAsync(deviceId, driverId);
                    break;
                case 31: // Güncelleme
                case 41:
                case 51:
                case 61:
                    await UpdateDeviceInKepServerAsync(deviceId, driverId);
                    break;
            }

            // İşlem başarılı, status'u güncelle
            await UpdateDeviceStatusAsync(deviceId, GetSuccessStatus(statusCode), driverId);

            _logger.LogInformation($"✅ İşlem tamamlandı: Device {deviceId}, Status {statusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"💥 İşlem başarısız: Device {deviceId}, Status {statusCode}");

            // Hata durumunda status'u güncelle
            await UpdateDeviceStatusAsync(deviceId, GetErrorStatus(statusCode), driverId);
        }
        finally
        {
            // Lock'u kaldır
            _operationLocks.TryRemove(deviceId, out _);
        }
    }

    private async void ProcessAllPendingOperations(object? state)
    {
        try
        {
            // Tüm bekleyen işlemleri al
            const string sql = @"
                SELECT cd.id, cd.statusCode, cd.driverId, cd.channelName
                FROM channeldevice cd
                WHERE cd.statusCode IN (11,20,21,31,41,51,61)
                ORDER BY cd.driverId, cd.id";

            var operations = await _dbManager.QueryExchangerAsync<dynamic>(sql);

            var groupedByDriver = operations.GroupBy(o => (int)o.driverId);

            foreach (var driverGroup in groupedByDriver)
            {
                var driverId = driverGroup.Key;

                foreach (var operation in driverGroup)
                {
                    var deviceId = (int)operation.id;
                    var statusCode = (byte)operation.statusCode;

                    // İşlem zaten devam ediyorsa atla
                    if (_operationLocks.ContainsKey(deviceId))
                    {
                        continue;
                    }

                    // Yeni işlem başlat
                    _ = Task.Run(() => ProcessDeviceOperationAsync(deviceId, statusCode, driverId));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending operations işlemi hatası");
        }
    }

    private async Task AddDeviceToKepServerAsync(int deviceId, int driverId)
    {
        try
        {
            // Device bilgilerini al - DÜZELTME: QueryFirstOrDefaultExchangerAsync -> QueryFirstExchangerAsync
            const string deviceSql = @"
                SELECT cd.id, cd.channelName, cd.deviceJson, cd.channelJson, cd.deviceTypeId
                FROM channeldevice cd
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId";

            var device = await _dbManager.QueryFirstExchangerAsync<dynamic>(deviceSql,
                new { DeviceId = deviceId, DriverId = driverId });

            if (device == null)
            {
                throw new Exception($"Device {deviceId} bulunamadı");
            }

            // Önce channel'ı oluştur/güncelle
            if (!string.IsNullOrEmpty(device.channelJson?.ToString()))
            {
                await _restApiManager.CreateOrUpdateChannelAsync(driverId, device.channelName, device.channelJson.ToString());
                await Task.Delay(200); // API delay
            }

            // Device'ı oluştur
            if (!string.IsNullOrEmpty(device.deviceJson?.ToString()))
            {
                await _restApiManager.CreateOrUpdateDeviceAsync(driverId, device.channelName, deviceId.ToString(), device.deviceJson.ToString());
                await Task.Delay(200); // API delay
            }

            // Tag'lari oluştur
            await CreateDeviceTagsAsync(deviceId, driverId, device.channelName);

            _logger.LogInformation($"✅ Device {deviceId} başarıyla KEP Server'a eklendi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} KEP Server'a eklenemedi");
            throw;
        }
    }

    private async Task RemoveDeviceFromKepServerAsync(int deviceId, int driverId)
    {
        try
        {
            // Device bilgilerini al - DÜZELTME: QueryFirstOrDefaultExchangerAsync -> QueryFirstExchangerAsync
            const string deviceSql = @"
                SELECT cd.channelName
                FROM channeldevice cd
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId";

            var device = await _dbManager.QueryFirstExchangerAsync<dynamic>(deviceSql,
                new { DeviceId = deviceId, DriverId = driverId });

            if (device != null)
            {
                // Önce tag'lari sil
                await RemoveDeviceTagsAsync(deviceId, driverId, device.channelName);
                await Task.Delay(200);

                // Device'ı sil
                await _restApiManager.DeleteDeviceAsync(driverId, device.channelName, deviceId.ToString());
                await Task.Delay(200);

                // Channel boşsa onu da sil (opsiyonel)
                await CheckAndRemoveEmptyChannelAsync(device.channelName, driverId);
            }

            _logger.LogInformation($"✅ Device {deviceId} başarıyla KEP Server'dan silindi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} KEP Server'dan silinemedi");
            throw;
        }
    }

    private async Task UpdateDeviceInKepServerAsync(int deviceId, int driverId)
    {
        try
        {
            // Önce sil sonra ekle stratejisi
            await RemoveDeviceFromKepServerAsync(deviceId, driverId);
            await Task.Delay(500);
            await AddDeviceToKepServerAsync(deviceId, driverId);

            _logger.LogInformation($"✅ Device {deviceId} başarıyla güncellendi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} güncellenemedi");
            throw;
        }
    }

    private async Task CreateDeviceTagsAsync(int deviceId, int driverId, string channelName)
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
                new { DeviceId = deviceId, DriverId = driverId });

            foreach (var tag in tags)
            {
                if (!string.IsNullOrEmpty(tag.tagJson?.ToString()))
                {
                    await _restApiManager.CreateOrUpdateTagAsync(driverId, channelName, deviceId.ToString(), tag.TagName, tag.tagJson.ToString());
                    await Task.Delay(50); // Tag'lar arası delay
                }
            }

            _logger.LogDebug($"✅ Device {deviceId} için {tags.Count()} tag oluşturuldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} tag'ları oluşturulamadı");
        }
    }

    private async Task RemoveDeviceTagsAsync(int deviceId, int driverId, string channelName)
    {
        try
        {
            // Device'ın tüm tag'larını al
            const string tagSql = @"
                SELECT JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName
                FROM channeldevice cd 
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = cd.deviceTypeId
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId
                
                UNION ALL
                
                SELECT JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName
                FROM channeldevice cd 
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = cd.id
                WHERE cd.id = @DeviceId AND cd.driverId = @DriverId";

            var tags = await _dbManager.QueryExchangerAsync<dynamic>(tagSql,
                new { DeviceId = deviceId, DriverId = driverId });

            foreach (var tag in tags)
            {
                await _restApiManager.DeleteTagAsync(driverId, channelName, deviceId.ToString(), tag.TagName);
                await Task.Delay(50); // Tag'lar arası delay
            }

            _logger.LogDebug($"✅ Device {deviceId} için {tags.Count()} tag silindi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} tag'ları silinemedi");
        }
    }

    private async Task CheckAndRemoveEmptyChannelAsync(string channelName, int driverId)
    {
        try
        {
            // Channel'da başka device var mı kontrol et
            const string checkSql = @"
                SELECT COUNT(*) 
                FROM channeldevice 
                WHERE channelName = @ChannelName 
                  AND driverId = @DriverId 
                  AND statusCode NOT IN (20,21,22,23)";

            var deviceCount = await _dbManager.QueryFirstExchangerAsync<int>(checkSql,
                new { ChannelName = channelName, DriverId = driverId });

            if (deviceCount == 0)
            {
                await _restApiManager.DeleteChannelAsync(driverId, channelName);
                _logger.LogDebug($"✅ Boş channel silindi: {channelName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Channel {channelName} kontrol edilemedi");
        }
    }

    private async Task UpdateDeviceStatusAsync(int deviceId, byte newStatus, int driverId)
    {
        try
        {
            const string sql = @"
                UPDATE channeldevice 
                SET statusCode = @NewStatus 
                WHERE id = @DeviceId AND driverId = @DriverId";

            await _dbManager.ExecuteExchangerAsync(sql,
                new { NewStatus = newStatus, DeviceId = deviceId, DriverId = driverId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device {deviceId} status güncellenemedi");
        }
    }

    private byte GetSuccessStatus(byte operationStatus)
    {
        return operationStatus switch
        {
            11 => 12, // Ekleme başarılı
            20 => 22, // Silme başarılı
            21 => 22, // Silme başarılı
            31 => 32, // Güncelleme başarılı
            41 => 42,
            51 => 52,
            61 => 62,
            _ => 12
        };
    }

    private byte GetErrorStatus(byte operationStatus)
    {
        return operationStatus switch
        {
            11 => 13, // Ekleme hatası
            20 => 23, // Silme hatası
            21 => 23, // Silme hatası
            31 => 33, // Güncelleme hatası
            41 => 43,
            51 => 53,
            61 => 63,
            _ => 13
        };
    }
}