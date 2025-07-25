// Koru1000.KepServerService/Program.cs (Güncelleme)
using Koru1000.DatabaseManager;
using Koru1000.KepServerService;
using Koru1000.KepServerService.Services;
using Koru1000.Shared;

var builder = Host.CreateApplicationBuilder(args);

// Mevcut services
var settings = SettingsManager.LoadSettings();
var dbManager = DatabaseManager.Instance(
    settings.Database.GetExchangerConnectionString(),
    settings.Database.GetKbinConnectionString());

builder.Services.AddSingleton(dbManager);

// ✅ Yeni services
builder.Services.AddSingleton<ISharedQueueService, SharedQueueService>();
builder.Services.AddSingleton<KepServerClientPool>();

builder.Services.AddHostedService<Worker>();

 var host = builder.Build();
host.Run();