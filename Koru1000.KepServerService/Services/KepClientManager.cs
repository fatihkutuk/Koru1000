using System.Collections.Concurrent;
using Koru1000.KepServerService.Models;
using Koru1000.KepServerService.Clients;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService.Services;

public class KepClientManager : IKepClientManager
{
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly KepServiceConfig _config;
    private readonly ILogger<KepClientManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<int, KepClient> _clients;
    private readonly Timer _statusTimer;
    private readonly IKepDataProcessor _dataProcessor;
    private bool _isRunning;

    public event EventHandler<KepDataChangedEventArgs>? DataChanged;
    public event EventHandler<KepStatusChangedEventArgs>? StatusChanged;

    public KepClientManager(
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        KepServiceConfig config,
        IKepDataProcessor dataProcessor,
        ILogger<KepClientManager> logger,
        ILoggerFactory loggerFactory)
    {
        _dbManager = dbManager;
        _config = config;
        _dataProcessor = dataProcessor;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _clients = new ConcurrentDictionary<int, KepClient>();

        _statusTimer = new Timer(CheckConnectionStatus, null,
            TimeSpan.FromSeconds(_config.StatusCheckIntervalSeconds),
            TimeSpan.FromSeconds(_config.StatusCheckIntervalSeconds));
    }

    public async Task StartAsync()
    {
        try
        {
            _logger.LogInformation("KEP Client Manager başlatılıyor...");
            _isRunning = true;

            await LoadClientsAsync();

            _logger.LogInformation($"KEP Client Manager başlatıldı. {_clients.Count} client aktif.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Client Manager başlatılamadı");
            throw;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _logger.LogInformation("KEP Client Manager durduruluyor...");
            _isRunning = false;

            _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

            var stopTasks = _clients.Values.Select(client => client.StopAsync());
            await Task.WhenAll(stopTasks);

            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();

            _logger.LogInformation("KEP Client Manager durduruldu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Client Manager durdurulamadı");
        }
    }

    private async Task LoadClientsAsync()
    {
        try
        {
            // Aktif client'ları al
            var clientIds = await GetActiveClientIdsAsync();
            _logger.LogInformation($"Found {clientIds.Count} active clients to load");

            foreach (var clientId in clientIds)
            {
                await CreateClientAsync(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client'lar yüklenirken hata");
            throw;
        }
    }

    private async Task<List<int>> GetActiveClientIdsAsync()
    {
        try
        {
            const string sql = @"
                SELECT cd.clientId 
                FROM channeldevice cd 
                WHERE cd.clientId IS NOT NULL 
                  AND cd.statusCode IN (11,31,41,51,61)
                GROUP BY cd.clientId 
                ORDER BY cd.clientId";

            var results = await _dbManager.QueryExchangerAsync<int>(sql);
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aktif client ID'leri alınamadı");
            return new List<int>();
        }
    }

    private async Task CreateClientAsync(int clientId)
    {
        try
        {
            _logger.LogInformation($"Client {clientId} oluşturuluyor...");

            var clientLogger = _loggerFactory.CreateLogger<KepClient>();

            var client = new KepClient(
                clientId,
                _config,
                _dbManager,
                clientLogger);

            client.DataChanged += OnClientDataChanged;
            client.StatusChanged += OnClientStatusChanged;

            await client.StartAsync();

            _clients.TryAdd(clientId, client);
            _logger.LogInformation($"Client {clientId} başarıyla oluşturuldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {clientId} oluşturulamadı");
        }
    }

    private void OnClientDataChanged(object? sender, KepDataChangedEventArgs e)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dataProcessor.ProcessDataChangedAsync(e);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Data processing hatası - Client: {e.ClientId}");
                }
            });

            DataChanged?.Invoke(sender, e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client data changed event işlenirken hata: {e.ClientId}");
        }
    }

    private void OnClientStatusChanged(object? sender, KepStatusChangedEventArgs e)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dataProcessor.ProcessStatusChangedAsync(e);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Status processing hatası - Client: {e.ClientId}");
                }
            });

            StatusChanged?.Invoke(sender, e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client status changed event işlenirken hata: {e.ClientId}");
        }
    }

    private void CheckConnectionStatus(object? state)
    {
        if (!_isRunning) return;

        foreach (var client in _clients.Values)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.CheckConnectionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Client {client.ClientId} status kontrolünde hata");
                }
            });
        }
    }

    public async Task<List<KepClientStatus>> GetClientStatusAsync()
    {
        var statusList = new List<KepClientStatus>();

        foreach (var client in _clients.Values)
        {
            var status = await client.GetStatusAsync();
            statusList.Add(status);
        }

        return statusList;
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client dispose hatası");
            }
        }

        _clients.Clear();
    }
}