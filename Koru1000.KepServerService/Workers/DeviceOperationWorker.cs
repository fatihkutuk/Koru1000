// Koru1000.KepServerService/Workers/DeviceOperationWorker.cs
using Koru1000.KepServerService.Services;

namespace Koru1000.KepServerService.Workers
{
    public class DeviceOperationWorker : BackgroundService
    {
        private readonly IDeviceOperationManager _operationManager;
        private readonly ILogger<DeviceOperationWorker> _logger;

        public DeviceOperationWorker(
            IDeviceOperationManager operationManager,
            ILogger<DeviceOperationWorker> logger)
        {
            _operationManager = operationManager;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("🚀 Device Operation Worker başlatılıyor...");

                await _operationManager.StartAsync();

                // Worker çalışırken bekle
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Device Operation Worker iptal edildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device Operation Worker hatası");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🛑 Device Operation Worker durduruluyor...");

            await _operationManager.StopAsync();
            await base.StopAsync(cancellationToken);

            _logger.LogInformation("✅ Device Operation Worker durduruldu");
        }
    }
}