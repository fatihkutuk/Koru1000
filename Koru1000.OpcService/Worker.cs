using Koru1000.OpcService.Services;

namespace Koru1000.OpcService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IOpcClientManager _opcClientManager;
    private readonly IOpcDataProcessor _dataProcessor;

    public Worker(
        ILogger<Worker> logger,
        IOpcClientManager opcClientManager,
        IOpcDataProcessor dataProcessor)
    {
        _logger = logger;
        _opcClientManager = opcClientManager;
        _dataProcessor = dataProcessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Koru1000 OPC Service baþlatýlýyor...");

            // OPC Client Manager'ý baþlat
            await _opcClientManager.StartAsync();

            // Data processor'ý baþlat
            await _dataProcessor.StartAsync();

            _logger.LogInformation("Koru1000 OPC Service baþarýyla baþlatýldý.");

            // Service çalýþýrken bekle
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC Service çalýþýrken hata oluþtu");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Koru1000 OPC Service durduruluyor...");

        try
        {
            await _dataProcessor.StopAsync();
            await _opcClientManager.StopAsync();

            _logger.LogInformation("Koru1000 OPC Service baþarýyla durduruldu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC Service durdurulurken hata");
        }

        await base.StopAsync(cancellationToken);
    }
}