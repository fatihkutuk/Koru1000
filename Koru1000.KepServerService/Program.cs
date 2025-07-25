// Koru1000.KepServerService/Program.cs
using Koru1000.Core.Models.OpcModels;
using Koru1000.DatabaseManager;
using Koru1000.KepServerService;
using Koru1000.KepServerService.Services;
using Koru1000.Shared;

var builder = Host.CreateApplicationBuilder(args);

// Logging yapılandırması
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Windows Service desteği
//builder.Services.AddWindowsService(options =>
//{
//    options.ServiceName = "Koru1000 KepServer Service";
//});

// Configuration
var config = new OpcServiceConfig
{
    ServiceName = "Koru1000 KepServer Service",
    MaxConcurrentDrivers = 10,
    StatusCheckIntervalSeconds = 60,
    Limits = new ClientLimits
    {
        MaxTagsPerSubscription = 3000, // Bu değeri 3000'e düşür test için
        PublishingIntervalMs = 1000,
        MaxNotificationsPerPublish = 5000,
        SessionTimeoutMs = 300000,
        ReconnectDelayMs = 5000
    }
};

builder.Services.AddSingleton(config);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();