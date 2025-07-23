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
            _logger.LogInformation("Koru1000 OPC Service ba�lat�l�yor...");

            // OPC Client Manager'� ba�lat
            await _opcClientManager.StartAsync();

            // Data processor'� ba�lat
            await _dataProcessor.StartAsync();

            _logger.LogInformation("Koru1000 OPC Service ba�ar�yla ba�lat�ld�.");

            // Service �al���rken bekle
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC Service �al���rken hata olu�tu");
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

            _logger.LogInformation("Koru1000 OPC Service ba�ar�yla durduruldu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC Service durdurulurken hata");
        }

        await base.StopAsync(cancellationToken);
    }
}