using Koru1000.DatabaseManager;
using Koru1000.KepServerService.Services;
using Koru1000.KepServerService.Workers;
using Koru1000.KepServerService.Models;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Koru1000.KepServerService;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Serilog yapılandırması
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File("logs/kepserver-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("🚀 Koru1000 KEP Server Service başlatılıyor...");

            var builder = Host.CreateApplicationBuilder(args);

            // Serilog ekle
            builder.Services.AddSerilog();

            // Ana konfigürasyon
            var config = new KepServiceConfig();
            builder.Services.AddSingleton(config);

            // Database Manager
            builder.Services.AddSingleton<DatabaseManager>();

            // Tüm driver'lar için manager'ları ekle
            builder.Services.AddSingleton<IMultiDriverManager, MultiDriverManager>();

            // KEP REST API Manager
            builder.Services.AddSingleton<IKepRestApiManager, KepRestApiManager>();

            // Device Operation Manager
            builder.Services.AddSingleton<IDeviceOperationManager, DeviceOperationManager>();

            // Ana worker servisleri - her driver için ayrı
            builder.Services.AddHostedService<MultiDriverWorker>();
            builder.Services.AddHostedService<DeviceOperationWorker>();

            var app = builder.Build();

            Log.Information("✅ Servis yapılandırması tamamlandı");

            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "💥 Servis başlatılamadı");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}