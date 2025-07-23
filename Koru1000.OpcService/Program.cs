using Koru1000.OpcService;
using Koru1000.OpcService.Services;
using Koru1000.Core.Models;
using Koru1000.Core.Models.OpcModels;
using Koru1000.Shared;
using Microsoft.Extensions.Hosting.WindowsServices; // Bu using eksikti

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Koru1000 OPC Service";
    })
    .ConfigureServices((hostContext, services) =>
    {
        // Konfigürasyonu yükle
        var settings = SettingsManager.LoadSettings();
        services.AddSingleton(settings);

        // OPC Service konfigürasyonu
        var opcConfig = LoadOpcServiceConfig();
        services.AddSingleton(opcConfig);

        // Database Manager
        var dbManager = Koru1000.DatabaseManager.DatabaseManager.Instance(
            settings.Database.GetExchangerConnectionString(),
            settings.Database.GetKbinConnectionString());
        services.AddSingleton(dbManager);

        // OPC Services - SIRALI OLARAK
        services.AddSingleton<IOpcDataProcessor, OpcDataProcessor>();
        services.AddSingleton<IOpcClientManager, OpcClientManager>();

        // Worker Service
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging((hostContext, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddEventLog();

        // OPC UA library logging
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

await host.RunAsync();

static OpcServiceConfig LoadOpcServiceConfig()
{
    return new OpcServiceConfig
    {
        ServiceName = "Koru1000 OPC Service",
        MaxConcurrentDrivers = 10,
        StatusCheckIntervalSeconds = 30,
        Limits = new ClientLimits
        {
            MaxTagsPerSubscription = 20000,
            MaxChannelsPerSession = 50,
            MaxDevicesPerSession = 50,
            PublishingIntervalMs = 1000,
            MaxNotificationsPerPublish = 10000
        }
    };
}