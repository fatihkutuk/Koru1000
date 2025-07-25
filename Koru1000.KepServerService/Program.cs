using Koru1000.KepServerService.Services;
using Koru1000.KepServerService.Models;
using Koru1000.Shared;
using Serilog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Serilog yapılandırması
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/kepserver-service-.txt",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        try
        {
            Log.Information("Koru1000 KEP Server Service başlatılıyor...");

            var builder = Host.CreateApplicationBuilder(args);

            // Windows Service desteği
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "Koru1000 KEP Server Service";
            });

            // Serilog
            builder.Services.AddSerilog();

            // Yapılandırma
            var serviceConfig = LoadServiceConfiguration();
            builder.Services.AddSingleton(serviceConfig);

            // Database Manager
            var settings = SettingsManager.LoadSettings();
            var dbManager = Koru1000.DatabaseManager.DatabaseManager.Instance(
                settings.Database.GetExchangerConnectionString(),
                settings.Database.GetKbinConnectionString());
            builder.Services.AddSingleton(dbManager);

            // Ana servisleri kaydet
            builder.Services.AddSingleton<IKepClientManager, KepClientManager>();
            builder.Services.AddSingleton<IKepDataProcessor, KepDataProcessor>();
            builder.Services.AddSingleton<IKepServerInitializer, KepServerInitializer>();
            builder.Services.AddSingleton<IKepRestApiManager, KepRestApiManager>();

            builder.Services.AddHostedService<KepServerWorker>();



            var host = builder.Build();

            Log.Information("Host oluşturuldu, servis başlatılıyor...");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Uygulama başlatılamadı");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static KepServiceConfig LoadServiceConfiguration()
    {
        return new KepServiceConfig
        {
            ServiceName = "Koru1000 KEP Server Service",
            ServiceDescription = "KEP Server OPC UA Client Service for Industrial Data Collection",
            MaxConcurrentClients = 5,
            DevicesPerClient = 1000, // Bu da veritabanından override edilecek
            StatusCheckIntervalSeconds = 30,
            RestartServiceOnError = true,
            KepServerServiceName = "KEPServerEXV6",
            AutoRestartKepServer = true,
            KepServerRestartDelay = 15000,
            // Bu ayarlar veritabanından gelecek, default değerler
            Limits = new KepClientLimits
            {
                MaxTagsPerClient = 20000,
                MaxDevicesPerClient = 1000,
                PublishingIntervalMs = 2000,
                MaxNotificationsPerPublish = 10000,
                SessionTimeoutMs = 360000,
                ReconnectDelayMs = 5000,
                MaxReconnectAttempts = 5
            },
            Security = new KepSecuritySettings
            {
                UseSecureConnection = true,
                AutoAcceptUntrustedCertificates = true,
                SecurityMode = "SignAndEncrypt",
                SecurityPolicy = "Basic256Sha256",
                UserTokenType = "UserName",
                Username = "",
                Password = ""
            },
            Connection = new KepConnectionSettings
            {
                EndpointUrl = "opc.tcp://localhost:49320",
                ConnectTimeoutMs = 15000,
                KeepAliveInterval = 10000,
                ReconnectPeriod = 5000
            },
            Logging = new KepLoggingSettings
            {
                EnableOpcTracing = true,
                LogLevel = "Information",
                LogDataChanges = false,
                LogConnectionStatus = true,
                LogPerformanceMetrics = true
            }
        };
    }
}