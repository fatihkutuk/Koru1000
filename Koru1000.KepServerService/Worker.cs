// Koru1000.KepServerService/Worker.cs
using Koru1000.Core.Models.OpcModels;
using Koru1000.DatabaseManager;
using Koru1000.KepServerService.Services;
using Koru1000.Shared;

namespace Koru1000.KepServerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly OpcServiceConfig _config;
        private FastKepClientManager? _kepManager;

        public Worker(ILogger<Worker> logger, OpcServiceConfig config)
        {
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("🚀 FAST KEP SERVICE başlatılıyor...");

                // Settings yükle
                var settings = SettingsManager.LoadSettings();
                if (settings?.Database == null)
                {
                    _logger.LogError("❌ Database settings bulunamadı!");
                    return;
                }

                // DatabaseManager oluştur
                var dbManager = DatabaseManager.DatabaseManager.Instance(
                    settings.Database.GetExchangerConnectionString(),
                    settings.Database.GetKbinConnectionString());

                // Bağlantıyı test et
                bool connected = await dbManager.TestExchangerConnectionAsync();
                if (!connected)
                {
                    _logger.LogError("❌ Database bağlantısı başarısız!");
                    return;
                }

                _logger.LogInformation("✅ Database bağlantısı başarılı");

                // Fast KEP Manager'ı başlat (ESKİ KOD GİBİ)
                _kepManager = new FastKepClientManager(dbManager, _config, _logger);
                await _kepManager.StartAsync();

                _logger.LogInformation("🎯 FAST KEP SERVICE başlatıldı");

                // Servis çalışır durumda kal
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEP Service başlatılamadı");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🛑 FAST KEP SERVICE durduruluyor...");

            if (_kepManager != null)
            {
                await _kepManager.StopAsync();
            }

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("✅ FAST KEP SERVICE durduruldu");
        }
    }
}