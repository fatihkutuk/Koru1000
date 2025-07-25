// Program.cs
using Koru1000.KepServerService;
using Koru1000.KepServerService.Services;
using Koru1000.Core.Models.OpcModels;
using Koru1000.Core.Models;
using Koru1000.Shared;

var builder = Host.CreateApplicationBuilder(args);

// Settings'i yükle
var settings = SettingsManager.LoadSettings();

// DatabaseManager'ı register et
builder.Services.AddSingleton(provider =>
    Koru1000.DatabaseManager.DatabaseManager.Instance(
        settings.Database.GetExchangerConnectionString(),
        settings.Database.GetKbinConnectionString()));

// ✅ Default ClientLimits register et (driver-specific olanlar runtime'da override edilecek)
builder.Services.AddSingleton<ClientLimits>(provider => new ClientLimits
{
    MaxTagsPerSubscription = 20000, // Default değerler
    MaxChannelsPerSession = 50,
    MaxDevicesPerSession = 50,
    MaxSubscriptionsPerSession = 10,
    PublishingIntervalMs = 1000,
    MaxNotificationsPerPublish = 10000,
    SessionTimeoutMs = 300000,
    ReconnectDelayMs = 5000,
    MaxReconnectAttempts = 5
});

// Diğer service'leri register et
builder.Services.AddSingleton<ISharedQueueService, SharedQueueService>();
builder.Services.AddSingleton<KepServerClientPool>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();