using Koru1000.DatabaseManager;
using Koru1000.Shared;
using Microsoft.Extensions.Logging; 

namespace Koru1000.KepServerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private DatabaseManager.DatabaseManager? _dbManager;
        private FastTagLoaderService? _tagLoader;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("🚀 KEPServerEX Service başlatılıyor...");

                // Database connection
                InitializeDatabase();

                // FastTagLoader initialize et
                _tagLoader = new FastTagLoaderService(_dbManager!, _logger);

                // Test: Driver'ları listele ve tag'larını yükle
                await TestDriversAndTagsAsync();

                // Ana loop
                while (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("💓 KEPServerEX Service çalışıyor: {time}", DateTimeOffset.Now);
                    await Task.Delay(30000, stoppingToken); // 30 saniye bekle
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX Service hatası");
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                var settings = SettingsManager.LoadSettings();
                _dbManager = DatabaseManager.DatabaseManager.Instance(
                    settings.Database.GetExchangerConnectionString(),
                    settings.Database.GetKbinConnectionString());

                _logger.LogInformation("✅ Database bağlantısı kuruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Database bağlantı hatası");
                throw;
            }
        }

        private async Task TestDriversAndTagsAsync()
        {
            try
            {
                if (_dbManager == null || _tagLoader == null) return;

                const string sql = @"
                    SELECT d.id, d.name, dt.name as driverTypeName
                    FROM driver d
                    INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                    WHERE dt.name = 'KEPSERVEREX'
                    ORDER BY d.id";

                var drivers = await _dbManager.QueryExchangerAsync<dynamic>(sql);

                _logger.LogInformation($"📋 Bulunan KEPSERVEREX driver'ları: {drivers.Count()}");

                foreach (var driver in drivers)
                {
                    _logger.LogInformation($"   • Driver ID: {driver.id}, Name: {driver.name}");

                    // Tag'larını yükle ve test et
                    var tags = await _tagLoader.GetDriverTagsAsync((int)driver.id);
                    _logger.LogInformation($"   📊 Driver {driver.name} has {tags.Count} tags");

                    // İlk 5 tag'i göster
                    foreach (var tag in tags.Take(5))
                    {
                        _logger.LogInformation($"     🏷️ {tag.NodeId}");
                    }

                    if (tags.Count > 5)
                    {
                        _logger.LogInformation($"     ... and {tags.Count - 5} more tags");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Driver ve tag testi başarısız");
            }
        }
    }
}