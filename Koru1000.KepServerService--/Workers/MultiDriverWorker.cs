using Koru1000.KepServerService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService.Workers;

public class MultiDriverWorker : BackgroundService
{
    private readonly IMultiDriverManager _multiDriverManager;
    private readonly ILogger<MultiDriverWorker> _logger;

    public MultiDriverWorker(
        IMultiDriverManager multiDriverManager,
        ILogger<MultiDriverWorker> logger)
    {
        _multiDriverManager = multiDriverManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("🚀 Multi Driver Worker başlatılıyor...");

            await _multiDriverManager.StartAllDriversAsync();

            // Worker çalışırken bekle
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Multi Driver Worker iptal edildi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Multi Driver Worker hatası");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Multi Driver Worker durduruluyor...");

        await _multiDriverManager.StopAllDriversAsync();
        await base.StopAsync(cancellationToken);

        _logger.LogInformation("✅ Multi Driver Worker durduruldu");
    }
}