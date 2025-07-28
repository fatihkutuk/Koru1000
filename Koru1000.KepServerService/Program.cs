using Koru1000.KepServerService.Services;
using Koru1000.KepServerService.Workers;
using Koru1000.KepServerService.Models;
using Koru1000.Core.Models;
using Koru1000.Shared;
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

            // UI'dan ayarları yükle
            var settings = LoadSettingsFromUI();
            if (settings?.Database == null)
            {
                Log.Error("❌ Veritabanı ayarları bulunamadı. Lütfen UI'dan ayarları yapılandırın.");
                return;
            }

            var builder = Host.CreateApplicationBuilder(args);

            // Serilog ekle
            builder.Services.AddSerilog();

            // Ana konfigürasyon
            var config = new KepServiceConfig();
            builder.Services.AddSingleton(config);

            // Database Manager - UI'DAN ALINAN AYARLARLA
            builder.Services.AddSingleton<Koru1000.DatabaseManager.DatabaseManager>(provider =>
            {
                var exchangerConn = settings.Database.GetExchangerConnectionString();
                var kbinConn = settings.Database.GetKbinConnectionString();

                Log.Information($"📊 Database bağlantıları:");
                Log.Information($"   • Exchanger: {settings.Database.ExchangerServer}:{settings.Database.ExchangerPort}/{settings.Database.ExchangerDatabase}");
                Log.Information($"   • Kbin: {settings.Database.KbinServer}:{settings.Database.KbinPort}/{settings.Database.KbinDatabase}");

                return Koru1000.DatabaseManager.DatabaseManager.Instance(exchangerConn, kbinConn);
            });

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

    private static AppSettings? LoadSettingsFromUI()
    {
        try
        {
            // UI'dan kaydedilmiş ayarları yükle
            var settings = SettingsManager.LoadSettings();

            if (settings?.Database == null)
            {
                Log.Warning("⚠️ UI'dan ayar bulunamadı, default ayarlar kullanılıyor");

                // Default ayarlar
                settings = new AppSettings
                {
                    Database = new DatabaseSettings
                    {
                        ExchangerServer = "localhost",
                        ExchangerPort = 3306,
                        ExchangerDatabase = "dbdataexchanger",
                        ExchangerUsername = "root",
                        ExchangerPassword = "",

                        KbinServer = "localhost",
                        KbinPort = 3306,
                        KbinDatabase = "kbindb",
                        KbinUsername = "root",
                        KbinPassword = ""
                    }
                };
            }

            return settings;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UI ayarları yüklenirken hata");
            return null;
        }
    }
}