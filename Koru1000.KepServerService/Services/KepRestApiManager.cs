using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;
using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services;

public interface IKepRestApiManager
{
    Task<bool> InitializeForDriverAsync(int driverId);
    Task<bool> CreateOrUpdateChannelAsync(int driverId, string channelName, string channelJson);
    Task<bool> CreateOrUpdateDeviceAsync(int driverId, string channelName, string deviceName, string deviceJson);
    Task<bool> CreateOrUpdateTagAsync(int driverId, string channelName, string deviceName, string tagName, string tagJson);
    Task<bool> DeleteChannelAsync(int driverId, string channelName);
    Task<bool> DeleteDeviceAsync(int driverId, string channelName, string deviceName);
    Task<bool> DeleteTagAsync(int driverId, string channelName, string deviceName, string tagName);
    Task<bool> TestConnectionAsync(int driverId);
}

public class KepRestApiManager : IKepRestApiManager
{
    private readonly ILogger<KepRestApiManager> _logger;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly Dictionary<int, HttpClient> _httpClients = new();
    private readonly Dictionary<int, RestApiSettings> _driverApiSettings = new();

    public KepRestApiManager(
        ILogger<KepRestApiManager> logger,
        Koru1000.DatabaseManager.DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    public async Task<bool> InitializeForDriverAsync(int driverId)
    {
        try
        {
            _logger.LogInformation($"🔧 Driver {driverId} için REST API ayarları yükleniyor...");

            // Driver'ın customSettings'ini al
            var driverSettings = await GetDriverSettingsAsync(driverId);
            if (driverSettings?.RestApiSettings == null)
            {
                _logger.LogWarning($"⚠️ Driver {driverId} için REST API ayarları bulunamadı, default kullanılıyor");
                driverSettings = new DriverCustomSettings
                {
                    RestApiSettings = new RestApiSettings()
                };
            }

            var apiSettings = driverSettings.RestApiSettings;
            _driverApiSettings[driverId] = apiSettings;

            // Bu driver için HttpClient oluştur
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(apiSettings.TimeoutSeconds);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Authentication ayarla
            if (apiSettings.Authentication?.Enabled == true)
            {
                var auth = apiSettings.Authentication;

                switch (auth.Type?.ToLower())
                {
                    case "basic":
                        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
                        _logger.LogDebug($"✅ Driver {driverId} Basic Auth ayarlandı: {auth.Username}");
                        break;

                    case "bearer":
                        if (!string.IsNullOrEmpty(auth.Token))
                        {
                            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
                            _logger.LogDebug($"✅ Driver {driverId} Bearer Token ayarlandı");
                        }
                        break;

                    case "apikey":
                        if (!string.IsNullOrEmpty(auth.ApiKey))
                        {
                            httpClient.DefaultRequestHeaders.Add("X-API-Key", auth.ApiKey);
                            _logger.LogDebug($"✅ Driver {driverId} API Key ayarlandı");
                        }
                        break;

                    default:
                        _logger.LogWarning($"⚠️ Driver {driverId} bilinmeyen auth type: {auth.Type}");
                        break;
                }
            }

            _httpClients[driverId] = httpClient;

            _logger.LogInformation($"✅ Driver {driverId} REST API ayarları başarıyla yüklendi");
            _logger.LogInformation($"   • Base URL: {apiSettings.BaseUrl}");
            _logger.LogInformation($"   • Timeout: {apiSettings.TimeoutSeconds}s");
            _logger.LogInformation($"   • Auth: {(apiSettings.Authentication?.Enabled == true ? apiSettings.Authentication.Type : "None")}");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {driverId} REST API ayarları yüklenemedi");
            return false;
        }
    }

    public async Task<bool> TestConnectionAsync(int driverId)
    {
        try
        {
            if (!_httpClients.TryGetValue(driverId, out var httpClient) ||
                !_driverApiSettings.TryGetValue(driverId, out var settings))
            {
                await InitializeForDriverAsync(driverId);
                if (!_httpClients.TryGetValue(driverId, out httpClient) ||
                    !_driverApiSettings.TryGetValue(driverId, out settings))
                {
                    return false;
                }
            }

            var response = await httpClient.GetAsync($"{settings.BaseUrl.TrimEnd('/')}/channels");
            var success = response.IsSuccessStatusCode;

            if (success)
            {
                _logger.LogInformation($"✅ Driver {driverId} KEP Server Config API bağlantısı başarılı");
            }
            else
            {
                _logger.LogWarning($"⚠️ Driver {driverId} KEP Server Config API bağlantı hatası: {response.StatusCode}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {driverId} KEP Server Config API bağlantı testi başarısız");
            return false;
        }
    }

    public async Task<bool> CreateOrUpdateChannelAsync(int driverId, string channelName, string channelJson)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}";

            // JSON'ı parse et ve FORCE_UPDATE ekle
            var channelObj = JObject.Parse(channelJson);
            channelObj["FORCE_UPDATE"] = true;

            var content = new StringContent(channelObj.ToString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PutAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug($"✅ Channel oluşturuldu/güncellendi: {channelName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Channel işlemi başarısız: {channelName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    public async Task<bool> CreateOrUpdateDeviceAsync(int driverId, string channelName, string deviceName, string deviceJson)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}/devices/{deviceName}";

            // JSON'ı parse et ve FORCE_UPDATE ekle
            var deviceObj = JObject.Parse(deviceJson);
            deviceObj["FORCE_UPDATE"] = true;

            var content = new StringContent(deviceObj.ToString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PutAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug($"✅ Device oluşturuldu/güncellendi: {channelName}.{deviceName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Device işlemi başarısız: {channelName}.{deviceName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    public async Task<bool> CreateOrUpdateTagAsync(int driverId, string channelName, string deviceName, string tagName, string tagJson)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}/devices/{deviceName}/tags/{tagName}";

            // JSON'ı parse et ve FORCE_UPDATE ekle
            var tagObj = JObject.Parse(tagJson);
            tagObj["FORCE_UPDATE"] = true;

            var content = new StringContent(tagObj.ToString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PutAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug($"✅ Tag oluşturuldu/güncellendi: {channelName}.{deviceName}.{tagName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Tag işlemi başarısız: {channelName}.{deviceName}.{tagName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    public async Task<bool> DeleteChannelAsync(int driverId, string channelName)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug($"✅ Channel silindi: {channelName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Channel silme başarısız: {channelName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    public async Task<bool> DeleteDeviceAsync(int driverId, string channelName, string deviceName)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}/devices/{deviceName}";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug($"✅ Device silindi: {channelName}.{deviceName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Device silme başarısız: {channelName}.{deviceName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    public async Task<bool> DeleteTagAsync(int driverId, string channelName, string deviceName, string tagName)
    {
        return await ExecuteWithRetryAsync(driverId, async (httpClient, settings) =>
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/channels/{channelName}/devices/{deviceName}/tags/{tagName}";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug($"✅ Tag silindi: {channelName}.{deviceName}.{tagName} (Driver: {driverId})");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"⚠️ Tag silme başarısız: {channelName}.{deviceName}.{tagName} - {response.StatusCode}: {errorContent}");
                return false;
            }
        });
    }

    // Helper metodlar
    private async Task<DriverCustomSettings?> GetDriverSettingsAsync(int driverId)
    {
        try
        {
            const string sql = @"
                SELECT d.customSettings
                FROM driver d
                INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                WHERE d.id = @DriverId AND dt.name = 'KEPSERVEREX'";

            var result = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });
            var driverData = result.FirstOrDefault();

            if (driverData?.customSettings != null)
            {
                return System.Text.Json.JsonSerializer.Deserialize<DriverCustomSettings>(
                    driverData.customSettings.ToString(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {driverId} ayarları alınamadı");
            return null;
        }
    }

    private async Task<bool> ExecuteWithRetryAsync(int driverId, Func<HttpClient, RestApiSettings, Task<bool>> operation)
    {
        if (!_httpClients.TryGetValue(driverId, out var httpClient) ||
            !_driverApiSettings.TryGetValue(driverId, out var settings))
        {
            await InitializeForDriverAsync(driverId);
            if (!_httpClients.TryGetValue(driverId, out httpClient) ||
                !_driverApiSettings.TryGetValue(driverId, out settings))
            {
                return false;
            }
        }

        var retryCount = settings.RetryCount;
        var retryDelay = settings.RetryDelayMs;

        for (int attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                var result = await operation(httpClient, settings);
                if (result || attempt == retryCount)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Driver {driverId} REST API işlemi başarısız (Deneme {attempt}/{retryCount})");

                if (attempt == retryCount)
                {
                    return false;
                }
            }

            if (attempt < retryCount)
            {
                await Task.Delay(retryDelay);
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var client in _httpClients.Values)
        {
            client?.Dispose();
        }
        _httpClients.Clear();
    }
}