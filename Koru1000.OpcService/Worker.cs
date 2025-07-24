// Koru1000.OpcService/Worker.cs
using Koru1000.OpcService.Services;

namespace Koru1000.OpcService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOpcClientManager _opcClientManager;
        private readonly IOpcDataProcessor _opcDataProcessor;

        public Worker(
            ILogger<Worker> logger,
            IOpcClientManager opcClientManager,
            IOpcDataProcessor opcDataProcessor)
        {
            _logger = logger;
            _opcClientManager = opcClientManager;
            _opcDataProcessor = opcDataProcessor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("🚀 Koru1000 OPC Service başlatılıyor...");

                // Data Processor'ı başlat
                await _opcDataProcessor.StartAsync();

                // OPC Client Manager'ı başlat
                await _opcClientManager.StartAsync();

                _logger.LogInformation("✅ Koru1000 OPC Service başarıyla başlatıldı!");

                // Service çalışmaya devam etsin
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Her 30 saniyede status kontrol et
                    var statuses = await _opcClientManager.GetServiceStatusAsync();

                    var connectedCount = statuses.Count(s => s.ConnectionStatus == Core.Models.OpcModels.OpcConnectionStatus.Connected);
                    _logger.LogInformation($"📊 OPC Service Status: {connectedCount}/{statuses.Count} drivers connected");

                    await Task.Delay(30000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Fatal error in OPC Service");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("🛑 Koru1000 OPC Service durduruluyor...");

                await _opcClientManager.StopAsync();
                await _opcDataProcessor.StopAsync();

                _logger.LogInformation("✅ Koru1000 OPC Service durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Error stopping OPC Service");
            }

            await base.StopAsync(cancellationToken);
        }
    }
}