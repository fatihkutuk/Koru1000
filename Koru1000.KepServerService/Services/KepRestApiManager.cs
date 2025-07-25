using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService.Services;

public interface IKepRestApiManager
{
    Task<string> ChannelPostAsync(string channelJson);
    Task<string> ChannelPutAsync(string channelJson, string channelName);
    Task<string> DevicePostAsync(string deviceJson, string channelName);
    Task<string> DevicePutAsync(string deviceJson, string channelName, string deviceName);
    Task<string> DeviceDeleteAsync(string channelName, string deviceName);
    Task<string> TagPostAsync(string tagJson, string channelName, string deviceName);
    Task<string> TagPutAsync(string tagJson, string channelName, string deviceName);
}

public class KepRestApiManager : IKepRestApiManager
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KepRestApiManager> _logger;
    private readonly string _baseUrl;
    private readonly string _credentials;

    public KepRestApiManager(ILogger<KepRestApiManager> logger)
    {
        _logger = logger;
        _baseUrl = "http://127.0.0.1:57412/config/v1/project/channels";
        _credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("administrator:Envest789.Korusu123"));

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> ChannelPostAsync(string channelJson)
    {
        try
        {
            var content = new StringContent(channelJson, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_baseUrl, content);

            await Task.Delay(25);

            if (response.IsSuccessStatusCode)
            {
                return "Success";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                if (errorContent.Contains("Validation failed on property common.ALLTYPES_NAME"))
                {
                    return "Exist";
                }
                return response.ReasonPhrase ?? "Failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel POST hatası");
            return "FAILED";
        }
    }

    public async Task<string> ChannelPutAsync(string channelJson, string channelName)
    {
        try
        {
            var channelObj = JsonSerializer.Deserialize<JsonDocument>(channelJson);
            var root = channelObj.RootElement.Clone();

            // FORCE_UPDATE ekle - Düzeltilmiş versiyonu
            var originalDict = root.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
            originalDict["FORCE_UPDATE"] = true;

            var modifiedJson = JsonSerializer.Serialize(originalDict);

            var content = new StringContent(modifiedJson, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}/{channelName}";
            var response = await _httpClient.PutAsync(url, content);

            await Task.Delay(50);

            return response.IsSuccessStatusCode ? "Success" : response.ReasonPhrase ?? "Failed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel PUT hatası: {ChannelName}", channelName);
            return "FAILED";
        }
    }

    public async Task<string> DevicePostAsync(string deviceJson, string channelName)
    {
        try
        {
            var content = new StringContent(deviceJson, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}/{channelName}/devices";
            var response = await _httpClient.PostAsync(url, content);

            await Task.Delay(25);

            if (response.IsSuccessStatusCode)
            {
                return "Success";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                if (errorContent.Contains("Validation failed on property common.ALLTYPES_NAME"))
                {
                    return "Exist";
                }
                return response.ReasonPhrase ?? "Failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device POST hatası: {ChannelName}", channelName);
            return "FAILED";
        }
    }

    public async Task<string> DevicePutAsync(string deviceJson, string channelName, string deviceName)
    {
        try
        {
            var deviceObj = JsonSerializer.Deserialize<JsonDocument>(deviceJson);
            var root = deviceObj.RootElement.Clone();

            // FORCE_UPDATE ekle - Düzeltilmiş versiyonu
            var originalDict = root.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
            originalDict["FORCE_UPDATE"] = true;

            var modifiedJson = JsonSerializer.Serialize(originalDict);

            var content = new StringContent(modifiedJson, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}/{channelName}/devices/{deviceName}";
            var response = await _httpClient.PutAsync(url, content);

            await Task.Delay(50);

            return response.IsSuccessStatusCode ? "Success" : response.ReasonPhrase ?? "Failed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device PUT hatası: {ChannelName}/{DeviceName}", channelName, deviceName);
            return "FAILED";
        }
    }

    public async Task<string> DeviceDeleteAsync(string channelName, string deviceName)
    {
        try
        {
            await Task.Delay(15000);

            var url = $"{_baseUrl}/{channelName}/devices/{deviceName}";
            var response = await _httpClient.DeleteAsync(url);

            await Task.Delay(300);

            return response.IsSuccessStatusCode ? "Success" : response.ReasonPhrase ?? "Failed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device DELETE hatası: {ChannelName}/{DeviceName}", channelName, deviceName);
            return "FAILED";
        }
    }

    public async Task<string> TagPostAsync(string tagJson, string channelName, string deviceName)
    {
        try
        {
            var content = new StringContent(tagJson, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}/{channelName}/devices/{deviceName}/tags";
            var response = await _httpClient.PostAsync(url, content);

            await Task.Delay(50);

            if (response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return "Success";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                if (errorContent.Contains("is already used"))
                {
                    return response.ReasonPhrase ?? "Success";
                }
                return "Failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tag POST hatası: {ChannelName}/{DeviceName}", channelName, deviceName);
            return "FAILED";
        }
    }

    public async Task<string> TagPutAsync(string tagJson, string channelName, string deviceName)
    {
        try
        {
            var deleteResult = await DeviceDeleteAsync(channelName, deviceName);
            if (deleteResult != "Success")
            {
                return "FAILED";
            }

            return "Success";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tag PUT hatası: {ChannelName}/{DeviceName}", channelName, deviceName);
            return "FAILED";
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}