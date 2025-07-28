using System.Collections.Concurrent;
using Koru1000.KepServerService.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Koru1000.KepServerService.Services;

public interface IMultiDriverManager
{
    Task StartAllDriversAsync();
    Task StopAllDriversAsync();
    Task<List<DriverInfo>> GetAllDriversAsync();
    Task<bool> RestartDriverAsync(int driverId);
    IKepDriverManager? GetDriverManager(int driverId);
}

public class MultiDriverManager : IMultiDriverManager
{
    private readonly ILogger<MultiDriverManager> _logger;
    private readonly DatabaseManager.DatabaseManager _dbManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<int, IKepDriverManager> _driverManagers = new();
    private readonly ConcurrentDictionary<int, DriverInfo> _drivers = new();

    public MultiDriverManager(
        ILogger<MultiDriverManager> logger,
        DatabaseManager.DatabaseManager dbManager,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _dbManager = dbManager;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAllDriversAsync()
    {
        try
        {
            _logger.LogInformation("🚀 Tüm driver'lar başlatılıyor...");

            var drivers = await LoadAllDriversAsync();

            foreach (var driver in drivers)
            {
                await StartDriverAsync(driver);
                await Task.Delay(2000); // Driver'lar arası delay
            }

            _logger.LogInformation($"✅ {drivers.Count} driver başarıyla başlatıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Driver'lar başlatılamadı");
            throw;
        }
    }

    public async Task StopAllDriversAsync()
    {
        try
        {
            _logger.LogInformation("🛑 Tüm driver'lar durduruluyor...");

            var tasks = _driverManagers.Values.Select(dm => dm.StopAsync());
            await Task.WhenAll(tasks);

            _driverManagers.Clear();
            _drivers.Clear();

            _logger.LogInformation("✅ Tüm driver'lar durduruldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Driver'lar durdurulamadı");
        }
    }

    public async Task<List<DriverInfo>> GetAllDriversAsync()
    {
        return _drivers.Values.ToList();
    }

    public async Task<bool> RestartDriverAsync(int driverId)
    {
        try
        {
            if (_driverManagers.TryGetValue(driverId, out var manager))
            {
                await manager.StopAsync();
                _driverManagers.TryRemove(driverId, out _);
            }

            if (_drivers.TryGetValue(driverId, out var driver))
            {
                await StartDriverAsync(driver);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver {driverId} yeniden başlatılamadı");
            return false;
        }
    }

    public IKepDriverManager? GetDriverManager(int driverId)
    {
        return _driverManagers.TryGetValue(driverId, out var manager) ? manager : null;
    }

    private async Task<List<DriverInfo>> LoadAllDriversAsync()
    {
        try
        {
            const string sql = @"
                SELECT d.id, d.name, dt.name as driverTypeName, d.customSettings
                FROM driver d
                INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                WHERE dt.name = 'KEPSERVEREX'
                ORDER BY d.id";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);
            var drivers = new List<DriverInfo>();

            foreach (var result in results)
            {
                var driver = new DriverInfo
                {
                    Id = (int)result.id,
                    Name = result.name,
                    DriverTypeName = result.driverTypeName
                };

                // CustomSettings JSON'ını parse et
                if (!string.IsNullOrEmpty(result.customSettings?.ToString()))
                {
                    try
                    {
                        driver.CustomSettings = JsonSerializer.Deserialize<DriverCustomSettings>(
                            result.customSettings.ToString(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DriverCustomSettings();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Driver {driver.Id} customSettings parse edilemedi");
                        driver.CustomSettings = new DriverCustomSettings();
                    }
                }
                else
                {
                    driver.CustomSettings = new DriverCustomSettings();
                }

                drivers.Add(driver);
                _drivers.TryAdd(driver.Id, driver);
            }

            _logger.LogInformation($"📚 {drivers.Count} driver bilgisi yüklendi");
            return drivers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driver bilgileri yüklenemedi");
            return new List<DriverInfo>();
        }
    }

    private async Task StartDriverAsync(DriverInfo driver)
    {
        try
        {
            _logger.LogInformation($"🚀 Driver başlatılıyor: {driver.Name} (ID: {driver.Id})");

            // Her driver için ayrı KepDriverManager oluştur
            var driverManager = new KepDriverManager(
                driver,
                _dbManager,
                _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<KepDriverManager>(),
                _serviceProvider.GetRequiredService<IKepRestApiManager>(),
                _serviceProvider.GetRequiredService<ILoggerFactory>(),
                _serviceProvider.GetRequiredService<IDeviceOperationManager>()); // EKLENEN

            await driverManager.StartAsync();

            _driverManagers.TryAdd(driver.Id, driverManager);

            _logger.LogInformation($"✅ Driver başlatıldı: {driver.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Driver başlatılamadı: {driver.Name}");
        }
    }
}