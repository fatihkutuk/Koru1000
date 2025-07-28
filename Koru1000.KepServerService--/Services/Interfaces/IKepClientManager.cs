// Koru1000.KepServerService/Services/Interfaces/IKepClientManager.cs
using Koru1000.KepServerService.Models;
using Koru1000.KepServerService.Clients;

namespace Koru1000.KepServerService.Services;

public interface IKepClientManager
{
    Task StartAsync();
    Task StopAsync();
    Task<List<KepClientStatus>> GetClientStatusAsync();
    Task UnsubscribeDeviceAsync(int clientId, int deviceId);
    Task RestartAffectedClientsAsync(int deviceId);
    Task<KepClient?> GetClientAsync(int clientId);

    event EventHandler<KepDataChangedEventArgs>? DataChanged;
    event EventHandler<KepStatusChangedEventArgs>? StatusChanged;
}

public interface IKepDataProcessor
{
    Task StartAsync();
    Task StopAsync();
    Task ProcessDataChangedAsync(KepDataChangedEventArgs e);
    Task ProcessStatusChangedAsync(KepStatusChangedEventArgs e);
}

public interface IKepServerInitializer
{
    Task<bool> InitializeKepServerAsync();
    Task<bool> RestartKepServerServiceAsync();
    Task<bool> SyncServerConfigurationAsync();
}