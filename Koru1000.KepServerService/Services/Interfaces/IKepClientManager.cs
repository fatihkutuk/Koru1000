using Koru1000.KepServerService.Models;

namespace Koru1000.KepServerService.Services;

public interface IKepClientManager
{
    Task StartAsync();
    Task StopAsync();
    Task<List<KepClientStatus>> GetClientStatusAsync();
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