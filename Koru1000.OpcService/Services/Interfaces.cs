using Koru1000.Core.Models.OpcModels;

namespace Koru1000.OpcService.Services
{
    public interface IOpcClientManager
    {
        Task StartAsync();
        Task StopAsync();
        Task<List<OpcServiceStatus>> GetServiceStatusAsync();
        event EventHandler<OpcDataChangedEventArgs> DataChanged;
        event EventHandler<OpcStatusChangedEventArgs> StatusChanged;
    }

    public interface IOpcDataProcessor
    {
        Task StartAsync();
        Task StopAsync();
        Task ProcessDataChangedAsync(OpcDataChangedEventArgs e);
        Task ProcessStatusChangedAsync(OpcStatusChangedEventArgs e);
    }
}