// Koru1000.KepServerService/Services/DeviceOperationManager.cs
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services
{
    public interface IDeviceOperationManager
    {
        Task StartAsync();
        Task StopAsync();
        event EventHandler<DeviceOperationEventArgs>? OperationCompleted;
        event EventHandler<DeviceOperationEventArgs>? OperationFailed;
    }

    public class DeviceOperationManager : IDeviceOperationManager
    {
        private readonly IKepRestApiManager _kepApi;
        private readonly IKepClientManager _clientManager;
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly ILogger<DeviceOperationManager> _logger;
        private readonly Timer _pollingTimer;
        private readonly SemaphoreSlim _processingLock;
        private readonly ConcurrentQueue<DeviceOperation> _operationQueue;
        private volatile bool _isRunning;

        public event EventHandler<DeviceOperationEventArgs>? OperationCompleted;
        public event EventHandler<DeviceOperationEventArgs>? OperationFailed;

        public DeviceOperationManager(
            IKepRestApiManager kepApi,
            IKepClientManager clientManager,
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            ILogger<DeviceOperationManager> logger)
        {
            _kepApi = kepApi;
            _clientManager = clientManager;
            _dbManager = dbManager;
            _logger = logger;
            _processingLock = new SemaphoreSlim(1, 1);
            _operationQueue = new ConcurrentQueue<DeviceOperation>();

            _pollingTimer = new Timer(CheckPendingOperationsCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task StartAsync()
        {
            _logger.LogInformation("🚀 Device Operation Manager başlatılıyor...");
            _isRunning = true;

            await CheckPendingOperationsAsync();
            _pollingTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            _logger.LogInformation("✅ Device Operation Manager başlatıldı");
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("🛑 Device Operation Manager durduruluyor...");
            _isRunning = false;

            _pollingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await ProcessQueuedOperations();

            _logger.LogInformation("✅ Device Operation Manager durduruldu");
        }

        private void CheckPendingOperationsCallback(object? state)
        {
            if (!_isRunning) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckPendingOperationsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Pending operations check hatası");
                }
            });
        }

        private async Task CheckPendingOperationsAsync()
        {
            var pendingOps = await GetPendingOperationsAsync();
            if (pendingOps.Any())
            {
                _logger.LogInformation($"📋 {pendingOps.Count} pending operation bulundu");

                foreach (var op in pendingOps)
                {
                    _operationQueue.Enqueue(op);
                }

                _ = Task.Run(ProcessQueuedOperations);
            }
        }

        private async Task<List<DeviceOperation>> GetPendingOperationsAsync()
        {
            const string sql = @"
                SELECT cd.id as DeviceId, cd.channelName as ChannelName, 
                       cd.statusCode, cd.deviceJson, cd.channelJson,
                       cd.deviceTypeId, cd.updateTime
                FROM channeldevice cd
                WHERE cd.statusCode IN (10,20,30,40,50,60)
                AND cd.updateTime > DATE_SUB(NOW(), INTERVAL 1 HOUR)
                ORDER BY cd.updateTime ASC";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);

            return results.Select(r => new DeviceOperation
            {
                DeviceId = (int)r.DeviceId,
                ChannelName = r.ChannelName ?? "",
                StatusCode = (byte)r.statusCode,
                DeviceJson = r.deviceJson ?? "{}",
                ChannelJson = r.channelJson ?? "{}",
                DeviceTypeId = r.deviceTypeId ?? 0,
                UpdateTime = r.updateTime
            }).ToList();
        }

        private async Task ProcessQueuedOperations()
        {
            await _processingLock.WaitAsync();
            try
            {
                while (_operationQueue.TryDequeue(out var operation))
                {
                    await ProcessSingleOperation(operation);
                    await Task.Delay(500);
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        private async Task ProcessSingleOperation(DeviceOperation operation)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"🔄 İşlem başlatılıyor: Device {operation.DeviceId}, Status {operation.StatusCode}");

                var result = operation.StatusCode switch
                {
                    DeviceStatusCodes.ADD_PENDING => await HandleAddDeviceAsync(operation),
                    DeviceStatusCodes.DELETE_PENDING => await HandleDeleteDeviceAsync(operation),
                    DeviceStatusCodes.UPDATE_PENDING => await HandleUpdateDeviceAsync(operation),
                    DeviceStatusCodes.ACTIVATE_PENDING => await HandleActivateDeviceAsync(operation),
                    DeviceStatusCodes.DEACTIVATE_PENDING => await HandleDeactivateDeviceAsync(operation),
                    DeviceStatusCodes.TAG_UPDATE_PENDING => await HandleTagUpdateAsync(operation),
                    _ => OperationResult.CreateFailure(
                        DeviceStatusCodes.GetFailedCode(operation.StatusCode),
                        $"Unsupported status code: {operation.StatusCode}")
                };

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;

                await ProcessOperationResult(operation, result);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, $"💥 İşlem exception: Device {operation.DeviceId}");

                await UpdateDeviceStatus(operation.DeviceId, DeviceStatusCodes.GetFailedCode(operation.StatusCode));

                OperationFailed?.Invoke(this, new DeviceOperationEventArgs
                {
                    DeviceId = operation.DeviceId,
                    StatusCode = operation.StatusCode,
                    Message = ex.Message,
                    Timestamp = DateTime.Now,
                    Success = false,
                    Duration = stopwatch.Elapsed
                });
            }
        }

        private async Task<OperationResult> HandleAddDeviceAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Channel kontrolü");
                var channelResult = await _kepApi.ChannelPostAsync(operation.ChannelJson);
                if (channelResult != "Success" && channelResult != "Exist")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.ADD_FAILED,
                        $"Channel oluşturulamadı: {channelResult}");
                }
                if (channelResult == "Success") result.Steps.Add("Channel oluşturuldu");

                result.Steps.Add("Device ekleme");
                var deviceResult = await _kepApi.DevicePostAsync(operation.DeviceJson, operation.ChannelName);
                if (deviceResult != "Success" && deviceResult != "Exist")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.ADD_FAILED,
                        $"Device oluşturulamadı: {deviceResult}");
                }
                result.Steps.Add("Device eklendi");

                var tagInfo = await GetDeviceTagInfoAsync(operation.DeviceId, operation.DeviceTypeId);
                if (tagInfo.TotalTagCount > 0)
                {
                    result.Steps.Add($"{tagInfo.TotalTagCount} tag ekleme");

                    if (!string.IsNullOrEmpty(tagInfo.DeviceTypeTagsJson) && tagInfo.DeviceTypeTagsJson != "[]")
                    {
                        var tagResult = await _kepApi.TagPostAsync(tagInfo.DeviceTypeTagsJson,
                            operation.ChannelName, operation.DeviceName);
                        if (tagResult != "Success")
                        {
                            result.Warnings.Add($"Device type tags uyarısı: {tagResult}");
                        }
                    }

                    if (!string.IsNullOrEmpty(tagInfo.IndividualTagsJson) && tagInfo.IndividualTagsJson != "[]")
                    {
                        var tagResult = await _kepApi.TagPostAsync(tagInfo.IndividualTagsJson,
                            operation.ChannelName, operation.DeviceName);
                        if (tagResult != "Success")
                        {
                            result.Warnings.Add($"Individual tags uyarısı: {tagResult}");
                        }
                    }

                    result.Steps.Add("Tag'lar eklendi");
                }

                await AssignDeviceToClientAsync(operation.DeviceId);
                result.Steps.Add("Client'a atandı");

                return OperationResult.CreateSuccess(DeviceStatusCodes.ADD_SUCCESS,
                    $"Device {operation.DeviceId} başarıyla eklendi", true);
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.ADD_FAILED,
                    $"Add operation hatası: {ex.Message}");
            }
        }

        private async Task<OperationResult> HandleDeleteDeviceAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Client subscription durduruluyor");
                var affectedClients = await GetDeviceClientsAsync(operation.DeviceId);
                foreach (var clientId in affectedClients)
                {
                    try
                    {
                        await _clientManager.UnsubscribeDeviceAsync(clientId, operation.DeviceId);
                        result.Steps.Add($"Client {clientId} unsubscribed");
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Client {clientId} unsubscribe hatası: {ex.Message}");
                    }
                }

                if (affectedClients.Any())
                {
                    await Task.Delay(2000);
                    result.Steps.Add("Subscription bekleme tamamlandı");
                }

                result.Steps.Add("KEP Server'dan silme");
                var deleteResult = await _kepApi.DeviceDeleteAsync(operation.ChannelName, operation.DeviceName);
                if (deleteResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.DELETE_FAILED,
                        $"KEP Server'dan silinemedi: {deleteResult}");
                }
                result.Steps.Add("KEP Server'dan silindi");

                return OperationResult.CreateSuccess(DeviceStatusCodes.DELETE_SUCCESS,
                    $"Device {operation.DeviceId} başarıyla silindi", affectedClients.Any());
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.DELETE_FAILED,
                    $"Delete operation hatası: {ex.Message}");
            }
        }

        private async Task<OperationResult> HandleUpdateDeviceAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Channel güncelleme");
                var channelResult = await _kepApi.ChannelPutAsync(operation.ChannelJson, operation.ChannelName);
                if (channelResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.UPDATE_FAILED,
                        $"Channel güncellenemedi: {channelResult}");
                }
                result.Steps.Add("Channel güncellendi");

                result.Steps.Add("Device güncelleme");
                var deviceResult = await _kepApi.DevicePutAsync(operation.DeviceJson,
                    operation.ChannelName, operation.DeviceName);
                if (deviceResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.UPDATE_FAILED,
                        $"Device güncellenemedi: {deviceResult}");
                }
                result.Steps.Add("Device güncellendi");

                var tagInfo = await GetDeviceTagInfoAsync(operation.DeviceId, operation.DeviceTypeId);
                if (tagInfo.TotalTagCount > 0)
                {
                    result.Steps.Add("Tag'lar güncelleniyor");

                    await _kepApi.DeviceDeleteAsync(operation.ChannelName, operation.DeviceName);
                    await Task.Delay(1000);

                    await _kepApi.DevicePostAsync(operation.DeviceJson, operation.ChannelName);

                    if (!string.IsNullOrEmpty(tagInfo.DeviceTypeTagsJson) && tagInfo.DeviceTypeTagsJson != "[]")
                    {
                        await _kepApi.TagPostAsync(tagInfo.DeviceTypeTagsJson, operation.ChannelName, operation.DeviceName);
                    }

                    if (!string.IsNullOrEmpty(tagInfo.IndividualTagsJson) && tagInfo.IndividualTagsJson != "[]")
                    {
                        await _kepApi.TagPostAsync(tagInfo.IndividualTagsJson, operation.ChannelName, operation.DeviceName);
                    }

                    result.Steps.Add("Tag'lar güncellendi");
                }

                return OperationResult.CreateSuccess(DeviceStatusCodes.UPDATE_SUCCESS,
                    $"Device {operation.DeviceId} başarıyla güncellendi", true);
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.UPDATE_FAILED,
                    $"Update operation hatası: {ex.Message}");
            }
        }

        private async Task<OperationResult> HandleActivateDeviceAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Data collection aktivasyonu");

                using var doc = JsonDocument.Parse(operation.DeviceJson);
                var deviceDict = new Dictionary<string, object>();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    deviceDict[property.Name] = property.Value.Clone();
                }

                deviceDict["servermain.DEVICE_DATA_COLLECTION"] = true;

                var updatedDeviceJson = JsonSerializer.Serialize(deviceDict);

                var deviceResult = await _kepApi.DevicePutAsync(updatedDeviceJson,
                    operation.ChannelName, operation.DeviceName);

                if (deviceResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.ACTIVATE_FAILED,
                        $"Device aktifleştirilemedi: {deviceResult}");
                }

                result.Steps.Add("Device aktifleştirildi");

                return OperationResult.CreateSuccess(DeviceStatusCodes.ACTIVATE_SUCCESS,
                    $"Device {operation.DeviceId} aktifleştirildi", true);
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.ACTIVATE_FAILED,
                    $"Activate operation hatası: {ex.Message}");
            }
        }

        private async Task<OperationResult> HandleDeactivateDeviceAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Data collection deaktivasyonu");

                using var doc = JsonDocument.Parse(operation.DeviceJson);
                var deviceDict = new Dictionary<string, object>();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    deviceDict[property.Name] = property.Value.Clone();
                }

                deviceDict["servermain.DEVICE_DATA_COLLECTION"] = false;

                var updatedDeviceJson = JsonSerializer.Serialize(deviceDict);

                var deviceResult = await _kepApi.DevicePutAsync(updatedDeviceJson,
                    operation.ChannelName, operation.DeviceName);

                if (deviceResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.DEACTIVATE_FAILED,
                        $"Device deaktifleştirilemedi: {deviceResult}");
                }

                result.Steps.Add("Device deaktifleştirildi");

                return OperationResult.CreateSuccess(DeviceStatusCodes.DEACTIVATE_SUCCESS,
                    $"Device {operation.DeviceId} deaktifleştirildi", true);
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.DEACTIVATE_FAILED,
                    $"Deactivate operation hatası: {ex.Message}");
            }
        }

        private async Task<OperationResult> HandleTagUpdateAsync(DeviceOperation operation)
        {
            try
            {
                var result = new OperationResult();

                result.Steps.Add("Tag update için device yeniden oluşturuluyor");

                await _kepApi.DeviceDeleteAsync(operation.ChannelName, operation.DeviceName);
                await Task.Delay(2000);

                var deviceResult = await _kepApi.DevicePostAsync(operation.DeviceJson, operation.ChannelName);
                if (deviceResult != "Success")
                {
                    return OperationResult.CreateFailure(DeviceStatusCodes.TAG_UPDATE_FAILED,
                        $"Device yeniden oluşturulamadı: {deviceResult}");
                }

                var tagInfo = await GetDeviceTagInfoAsync(operation.DeviceId, operation.DeviceTypeId);
                if (tagInfo.TotalTagCount > 0)
                {
                    if (!string.IsNullOrEmpty(tagInfo.DeviceTypeTagsJson) && tagInfo.DeviceTypeTagsJson != "[]")
                    {
                        await _kepApi.TagPostAsync(tagInfo.DeviceTypeTagsJson, operation.ChannelName, operation.DeviceName);
                    }

                    if (!string.IsNullOrEmpty(tagInfo.IndividualTagsJson) && tagInfo.IndividualTagsJson != "[]")
                    {
                        await _kepApi.TagPostAsync(tagInfo.IndividualTagsJson, operation.ChannelName, operation.DeviceName);
                    }

                    result.Steps.Add($"{tagInfo.TotalTagCount} tag güncellendi");
                }

                return OperationResult.CreateSuccess(DeviceStatusCodes.TAG_UPDATE_SUCCESS,
                    $"Device {operation.DeviceId} tag'ları güncellendi", true);
            }
            catch (Exception ex)
            {
                return OperationResult.CreateFailure(DeviceStatusCodes.TAG_UPDATE_FAILED,
                    $"Tag update hatası: {ex.Message}");
            }
        }

        private async Task<DeviceTagInfo> GetDeviceTagInfoAsync(int deviceId, int deviceTypeId)
        {
            var tagInfo = new DeviceTagInfo
            {
                DeviceId = deviceId,
                ChannelName = "",
                DeviceName = deviceId.ToString()
            };

            try
            {
                const string deviceTypeTagsSql = "CALL sp_getDeviceTagjSons(@DeviceId)";
                tagInfo.DeviceTypeTagsJson = await _dbManager.QueryFirstExchangerAsync<string>(
                    deviceTypeTagsSql, new { DeviceId = deviceId }) ?? "[]";

                const string individualTagsSql = "CALL sp_getDeviceIndividualTagJsons(@DeviceId)";
                tagInfo.IndividualTagsJson = await _dbManager.QueryFirstExchangerAsync<string>(
                    individualTagsSql, new { DeviceId = deviceId }) ?? "[]";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Tag info alınırken hata: Device {deviceId}");
            }

            return tagInfo;
        }

        private async Task<List<int>> GetDeviceClientsAsync(int deviceId)
        {
            try
            {
                const string sql = "SELECT DISTINCT clientId FROM channeldevice WHERE id = @DeviceId AND clientId IS NOT NULL";
                var results = await _dbManager.QueryExchangerAsync<int>(sql, new { DeviceId = deviceId });
                return results.ToList();
            }
            catch
            {
                return new List<int>();
            }
        }

        private async Task AssignDeviceToClientAsync(int deviceId)
        {
            try
            {
                const string sql = @"
                    SELECT clientId, COUNT(*) as DeviceCount 
                    FROM channeldevice 
                    WHERE clientId IS NOT NULL 
                    GROUP BY clientId 
                    ORDER BY COUNT(*) ASC 
                    LIMIT 1";

                var result = await _dbManager.QueryFirstExchangerAsync<dynamic>(sql);

                if (result != null)
                {
                    int targetClientId = (int)result.clientId;

                    const string updateSql = "UPDATE channeldevice SET clientId = @ClientId WHERE id = @DeviceId";
                    await _dbManager.ExecuteExchangerAsync(updateSql,
                        new { ClientId = targetClientId, DeviceId = deviceId });

                    _logger.LogInformation($"📋 Device {deviceId} client {targetClientId}'a atandı");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Device {deviceId} client assignment hatası");
            }
        }

        private async Task ProcessOperationResult(DeviceOperation operation, OperationResult result)
        {
            await UpdateDeviceStatus(operation.DeviceId, result.ResultStatusCode);

            var eventArgs = new DeviceOperationEventArgs
            {
                DeviceId = operation.DeviceId,
                StatusCode = operation.StatusCode,
                Message = result.Message,
                Timestamp = DateTime.Now,
                Success = result.Success,
                Duration = result.Duration
            };

            if (result.Success)
            {
                _logger.LogInformation($"✅ İşlem başarılı ({result.Duration.TotalMilliseconds:F0}ms): {result.Message}");
                if (result.Steps.Any())
                {
                    _logger.LogInformation($"📝 Adımlar: {string.Join(" → ", result.Steps)}");
                }
                if (result.Warnings.Any())
                {
                    _logger.LogWarning($"⚠️ Uyarılar: {string.Join(", ", result.Warnings)}");
                }

                if (result.RequiresClientRestart)
                {
                    _logger.LogInformation($"🔄 Client restart gerekiyor: Device {operation.DeviceId}");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        await _clientManager.RestartAffectedClientsAsync(operation.DeviceId);
                    });
                }

                OperationCompleted?.Invoke(this, eventArgs);
            }
            else
            {
                _logger.LogError($"❌ İşlem başarısız ({result.Duration.TotalMilliseconds:F0}ms): {result.Message}");
                OperationFailed?.Invoke(this, eventArgs);
            }
        }

        private async Task UpdateDeviceStatus(int deviceId, byte statusCode)
        {
            try
            {
                const string sql = @"
                    UPDATE channeldevice 
                    SET statusCode = @StatusCode, updateTime = NOW() 
                    WHERE id = @DeviceId";

                await _dbManager.ExecuteExchangerAsync(sql,
                    new { DeviceId = deviceId, StatusCode = statusCode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Status update hatası: Device {deviceId}, Status {statusCode}");
            }
        }

        public void Dispose()
        {
            _pollingTimer?.Dispose();
            _processingLock?.Dispose();
        }
    }
}