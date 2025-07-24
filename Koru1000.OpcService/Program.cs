// Koru1000.OpcService/Program.cs
using Koru1000.OpcService;
using Koru1000.OpcService.Services;
using Koru1000.Core.Models.OpcModels;
using Koru1000.Shared;
using Microsoft.Extensions.Hosting.WindowsServices;

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

        // OPC Services
        services.AddSingleton<IOpcClientManager, OpcClientManager>();
        services.AddSingleton<IOpcDataProcessor, OpcDataProcessor>();

        // Worker Service
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging((hostContext, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddEventLog();

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
            MaxTagsPerSubscription = 20000,     // 20000 tag per client
            MaxChannelsPerSession = 50,
            MaxDevicesPerSession = 50,
            PublishingIntervalMs = 2000,
            MaxNotificationsPerPublish = 5000,  // 5000'e düþür
            SessionTimeoutMs = 600000,
            ReconnectDelayMs = 10000,
            MaxReconnectAttempts = 5
        }
    };
}