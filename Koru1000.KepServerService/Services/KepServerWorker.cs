using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services;

public class KepServerWorker : BackgroundService
{
    private readonly ILogger<KepServerWorker> _logger;
    private readonly KepServiceConfig _config;
    private readonly IKepServerInitializer _initializer;
    private readonly IKepClientManager _clientManager;
    private readonly IKepDataProcessor _dataProcessor;

    public KepServerWorker(
        ILogger<KepServerWorker> logger,
        KepServiceConfig config,
        IKepServerInitializer initializer,
        IKepClientManager clientManager,
        IKepDataProcessor dataProcessor)
    {
        _logger = logger;
        _config = config;
        _initializer = initializer;
        _clientManager = clientManager;
        _dataProcessor = dataProcessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("KEP Server Worker başlatılıyor...");

            // KEP Server'ı başlat ve senkronize et
            if (!await _initializer.InitializeKepServerAsync())
            {
                _logger.LogError("KEP Server başlatılamadı, servis durduruluyor");
                return;
            }

            // Data processor'ı başlat
            await _dataProcessor.StartAsync();

            // Client manager'ı başlat
            await _clientManager.StartAsync();

            _logger.LogInformation("KEP Server Worker başarıyla başlatıldı");

            // Ana çalışma döngüsü
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Periyodik status kontrolü
                    var clientStatuses = await _clientManager.GetClientStatusAsync();

                    var totalClients = clientStatuses.Count;
                    var connectedClients = clientStatuses.Count(s => s.Status == "Ok");
                    var totalMessages = clientStatuses.Sum(s => s.TotalMessagesReceived);

                    _logger.LogInformation($"Status: {connectedClients}/{totalClients} clients connected, {totalMessages} total messages processed");

                    // Status check interval kadar bekle
                    await Task.Delay(TimeSpan.FromSeconds(_config.StatusCheckIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker döngüsünde hata");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Server Worker başlatılamadı");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("KEP Server Worker durduruluyor...");

        try
        {
            await _clientManager.StopAsync();
            await _dataProcessor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker durdurma sırasında hata");
        }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("KEP Server Worker durduruldu");
    }
}