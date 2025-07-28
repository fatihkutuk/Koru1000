using System.ServiceProcess;
using System.Text.Json;
using Koru1000.KepServerService.Models;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Koru1000.KepServerService.Services;

public class KepServerInitializer : IKepServerInitializer
{
    private readonly KepServiceConfig _config;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly ILogger<KepServerInitializer> _logger;
    private readonly IKepRestApiManager _restApiManager;
    private Session? _session;
    private ApplicationConfiguration? _appConfig;
    private DriverCustomSettings? _driverInfo;

    // Metrikler için properties
    private SyncMetrics _metrics = new SyncMetrics();

    public KepServerInitializer(
        KepServiceConfig config,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        IKepRestApiManager restApiManager,
        ILogger<KepServerInitializer> logger)
    {
        _config = config;
        _dbManager = dbManager;
        _restApiManager = restApiManager;
        _logger = logger;
    }

    public async Task<bool> InitializeKepServerAsync()
    {
        try
        {
            _logger.LogInformation("🚀 KEP Server başlatılıyor...");

            // Driver ayarlarını veritabanından yükle
            if (!await LoadDriverSettingsAsync())
            {
                _logger.LogError("❌ Driver ayarları yüklenemedi");
                return false;
            }

            if (_config.AutoRestartKepServer)
            {
                if (!await RestartKepServerServiceAsync())
                {
                    _logger.LogError("❌ KEP Server servisi başlatılamadı");
                    return false;
                }

                _logger.LogInformation($"⏳ KEP Server restart delay: {_config.KepServerRestartDelay}ms");
                await Task.Delay(_config.KepServerRestartDelay);
            }

            if (!await CreateOpcSessionAsync())
            {
                _logger.LogError("❌ OPC UA Session oluşturulamadı");
                return false;
            }

            if (!await SyncServerConfigurationAsync())
            {
                _logger.LogError("❌ Server konfigürasyonu senkronize edilemedi");
                return false;
            }

            _logger.LogInformation("✅ KEP Server başarıyla başlatıldı");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 KEP Server başlatılamadı");
            return false;
        }
    }

    private async Task<bool> LoadDriverSettingsAsync()
    {
        try
        {
            _logger.LogInformation("📚 Driver ayarları yükleniyor...");

            const string sql = @"
                SELECT d.id, d.name, dt.name as driverTypeName, d.customSettings
                FROM driver d
                INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                WHERE dt.name = 'KEPSERVEREX'
                ORDER BY d.id
                LIMIT 1";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);
            var driverData = results.FirstOrDefault();

            if (driverData == null)
            {
                _logger.LogError("❌ KEPSERVEREX driver bulunamadı");
                return false;
            }

            _driverInfo = new DriverCustomSettings
            {
                Id = (int)driverData.id,
                Name = driverData.name,
                DriverTypeName = driverData.driverTypeName
            };

            // CustomSettings JSON'ını parse et
            if (!string.IsNullOrEmpty(driverData.customSettings?.ToString()))
            {
                try
                {
                    _driverInfo.CustomSettings = JsonSerializer.Deserialize<DriverCustomSettings>(
                        driverData.customSettings.ToString(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DriverCustomSettings();

                    _logger.LogInformation($"✅ Driver ayarları yüklendi - Endpoint: {_driverInfo.CustomSettings.EndpointUrl}, Security: {_driverInfo.CustomSettings.Security.Mode}");
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "⚠️ CustomSettings JSON parse hatası, default ayarlar kullanılıyor");
                    _driverInfo.CustomSettings = new DriverCustomSettings();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Driver ayarları yüklenirken hata");
            return false;
        }
    }

    private async Task<bool> CreateOpcSessionAsync()
    {
        try
        {
            if (_driverInfo?.CustomSettings == null)
            {
                _logger.LogError("❌ Driver ayarları yüklenmemiş");
                return false;
            }

            await CreateApplicationConfigurationAsync();

            var endpointUrl = _driverInfo.CustomSettings.EndpointUrl;
            _logger.LogInformation($"🔗 OPC UA bağlantısı kuruluyor: {endpointUrl}");
            _logger.LogInformation($"🔒 Security Mode: {_driverInfo.CustomSettings.Security.Mode}");
            _logger.LogInformation($"👤 User: {_driverInfo.CustomSettings.Credentials.Username}");

            // Security ayarına göre endpoint seç
            bool useSecurity = _driverInfo.CustomSettings.Security.Mode != "None";
            var endpointDescription = CoreClientUtils.SelectEndpoint(_appConfig, endpointUrl, useSecurity: useSecurity);

            var endpointConfiguration = EndpointConfiguration.Create(_appConfig);
            endpointConfiguration.OperationTimeout = _config.Connection.ConnectTimeoutMs;

            var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

            // User identity - driver ayarlarından al
            IUserIdentity userIdentity;
            if (_driverInfo.CustomSettings.Security.UserTokenType == "UserName" &&
                !string.IsNullOrEmpty(_driverInfo.CustomSettings.Credentials.Username))
            {
                userIdentity = new UserIdentity(
                    _driverInfo.CustomSettings.Credentials.Username,
                    _driverInfo.CustomSettings.Credentials.Password);
                _logger.LogInformation($"🔐 Username authentication kullanılıyor: {_driverInfo.CustomSettings.Credentials.Username}");
            }
            else
            {
                userIdentity = new UserIdentity();
                _logger.LogInformation("🔓 Anonymous authentication kullanılıyor");
            }

            _session = await Session.Create(
                _appConfig,
                endpoint,
                false,
                "Koru1000 KEP Server Init Session",
                (uint)_driverInfo.CustomSettings.ConnectionSettings.SessionTimeout,
                userIdentity,
                null);

            _logger.LogInformation("✅ OPC UA Session başarıyla oluşturuldu");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 OPC UA Session oluşturulamadı");
            return false;
        }
    }

    private async Task CreateApplicationConfigurationAsync()
    {
        if (_driverInfo?.CustomSettings == null) return;

        var certificateSubjectName = $"CN=Koru1000 KEP Server Init, C=US, S=Arizona, O=OPC Foundation, DC={Utils.GetHostName()}";

        _appConfig = new ApplicationConfiguration()
        {
            ApplicationName = "Koru1000 KEP Server Initializer",
            ApplicationUri = "urn:localhost:Koru1000:KepServerInit",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = @"Directory",
                    StorePath = @"%LocalApplicationData%\OPC Foundation\pki\own",
                    SubjectName = certificateSubjectName
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = @"Directory",
                    StorePath = @"%LocalApplicationData%\OPC Foundation\pki\issuer"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = @"Directory",
                    StorePath = @"%LocalApplicationData%\OPC Foundation\pki\trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = @"Directory",
                    StorePath = @"%LocalApplicationData%\OPC Foundation\pki\rejected"
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = _config.Connection.ConnectTimeoutMs,
                MaxStringLength = 1048576,
                MaxByteStringLength = 1048576,
                MaxArrayLength = 65535,
                MaxMessageSize = 4194304,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = _driverInfo.CustomSettings.ConnectionSettings.SessionTimeout
            }
        };

        await _appConfig.Validate(ApplicationType.Client);

        var applicationInstance = new ApplicationInstance(_appConfig);
        bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 240);

        if (!certificateValid)
        {
            throw new Exception("Application instance certificate invalid!");
        }

        _appConfig.CertificateValidator.CertificateValidation += (s, e) => {
            _logger.LogDebug("Certificate validation: Subject='{Subject}' - ACCEPTING", e.Certificate?.Subject);
            e.Accept = true;
        };
    }

    public async Task<bool> RestartKepServerServiceAsync()
    {
        try
        {
            _logger.LogInformation($"🔄 KEP Server servisi yeniden başlatılıyor: {_config.KepServerServiceName}");

            using var service = new ServiceController(_config.KepServerServiceName);

            var timeout = TimeSpan.FromMinutes(2);

            switch (service.Status)
            {
                case ServiceControllerStatus.Running:
                    _logger.LogInformation("⏹️ Servis durduruluyor...");
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    break;

                case ServiceControllerStatus.StopPending:
                    _logger.LogInformation("⏳ Servisin durması bekleniyor...");
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    break;
            }

            if (service.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("▶️ Servis başlatılıyor...");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }

            var isRunning = service.Status == ServiceControllerStatus.Running;
            _logger.LogInformation($"📊 KEP Server servisi durumu: {service.Status}");

            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"💥 KEP Server servisi yeniden başlatılamadı: {_config.KepServerServiceName}");
            return false;
        }
    }

    public async Task<bool> SyncServerConfigurationAsync()
    {
        try
        {
            _logger.LogInformation("🔄 Server konfigürasyonu senkronize ediliyor...");

            _metrics = new SyncMetrics();
            var syncStartTime = DateTime.Now;

            // 1. Detaylı analizler
            var tagAnalysis = await AnalyzeTagCountsAsync();
            var clientAnalysis = await AnalyzeClientIssuesAsync();

            // 2. Database metrikleri
            await CollectDatabaseMetricsAsync();

            // 3. KEP Server metrikleri
            await CollectKepServerMetricsAsync();

            // 4. Senkronizasyon
            await PerformDetailedSyncAsync();

            // 5. Client dağıtımı - EKSİK CLIENT'LARI DÜZELT
            await FixMissingClientsAsync(clientAnalysis);

            // 6. Final kontrol
            await CollectFinalMetricsAsync();

            _metrics.TotalSyncDuration = DateTime.Now - syncStartTime;

            // 7. Tag sayısı uyarısı
            if (tagAnalysis.DatabaseTotalExpectedTags > 200000)
            {
                _logger.LogWarning($"⚠️ ÇOK FAZLA TAG: {tagAnalysis.DatabaseTotalExpectedTags:N0} tag bekleniyor!");
                _logger.LogWarning($"   KEP Server performans sorunları yaşayabilir");
                _logger.LogWarning($"   Tag sayısını azaltmayı veya client sayısını artırmayı düşünün");
            }

            LogFinalReport();
            LogTagDiscrepancy(tagAnalysis);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Server konfigürasyonu senkronize edilemedi");
            return false;
        }
    }

    private async Task<TagAnalysisResult> AnalyzeTagCountsAsync()
    {
        try
        {
            _logger.LogInformation("🏷️ Tag analizi başlatılıyor...");

            var result = new TagAnalysisResult();

            // Database'den tag sayıları
            const string tagCountSql = @"
                SELECT 
                    COUNT(*) as TotalActiveDevices,
                    SUM(CASE WHEN dt.allTagJsons IS NOT NULL AND dt.allTagJsons != '[]' THEN JSON_LENGTH(dt.allTagJsons) ELSE 0 END) as TypeTagCount,
                    SUM(CASE WHEN cd.individualTags IS NOT NULL AND cd.individualTags != '[]' THEN JSON_LENGTH(cd.individualTags) ELSE 0 END) as IndividualTagCount
                FROM channeldevice cd
                LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
                WHERE cd.statusCode IN (11,31,41,51,61)";

            var tagCounts = await _dbManager.QueryFirstExchangerAsync<dynamic>(tagCountSql);
            result.DatabaseActiveDevices = (int)tagCounts.TotalActiveDevices;
            result.DatabaseTypeTagCount = (long)(tagCounts.TypeTagCount ?? 0);
            result.DatabaseIndividualTagCount = (long)(tagCounts.IndividualTagCount ?? 0);
            result.DatabaseTotalExpectedTags = result.DatabaseTypeTagCount + result.DatabaseIndividualTagCount;

            // Device başına detaylı analiz
            const string deviceTagSql = @"
                SELECT 
                    cd.id as DeviceId,
                    cd.channelName,
                    cd.statusCode,
                    cd.clientId,
                    CASE WHEN dt.allTagJsons IS NOT NULL AND dt.allTagJsons != '[]' THEN JSON_LENGTH(dt.allTagJsons) ELSE 0 END as TypeTags,
                    CASE WHEN cd.individualTags IS NOT NULL AND cd.individualTags != '[]' THEN JSON_LENGTH(cd.individualTags) ELSE 0 END as IndividualTags
                FROM channeldevice cd
                LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
                WHERE cd.statusCode IN (11,31,41,51,61)
                ORDER BY (TypeTags + IndividualTags) DESC
                LIMIT 20";

            var deviceDetails = await _dbManager.QueryExchangerAsync<dynamic>(deviceTagSql);

            foreach (var device in deviceDetails)
            {
                var detail = new DeviceTagDetail
                {
                    DeviceId = (int)device.DeviceId,
                    ChannelName = device.channelName?.ToString() ?? "",
                    ClientId = device.clientId ?? 0,
                    StatusCode = (byte)device.statusCode,
                    TypeTagCount = (int)(device.TypeTags ?? 0),
                    IndividualTagCount = (int)(device.IndividualTags ?? 0)
                };
                detail.TotalTags = detail.TypeTagCount + detail.IndividualTagCount;
                result.TopDevices.Add(detail);
            }

            // Client dağılımı
            const string clientSql = @"
                SELECT 
                    cd.clientId,
                    COUNT(*) as DeviceCount,
                    SUM(CASE WHEN dt.allTagJsons IS NOT NULL AND dt.allTagJsons != '[]' THEN JSON_LENGTH(dt.allTagJsons) ELSE 0 END) as TypeTags,
                    SUM(CASE WHEN cd.individualTags IS NOT NULL AND cd.individualTags != '[]' THEN JSON_LENGTH(cd.individualTags) ELSE 0 END) as IndividualTags
                FROM channeldevice cd
                LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
                WHERE cd.statusCode IN (11,31,41,51,61) AND cd.clientId IS NOT NULL
                GROUP BY cd.clientId
                ORDER BY cd.clientId";

            var clientDetails = await _dbManager.QueryExchangerAsync<dynamic>(clientSql);

            foreach (var client in clientDetails)
            {
                var detail = new ClientTagDetail
                {
                    ClientId = (int)client.clientId,
                    DeviceCount = (int)client.DeviceCount,
                    TypeTagCount = (long)(client.TypeTags ?? 0),
                    IndividualTagCount = (long)(client.IndividualTags ?? 0)
                };
                detail.TotalTags = detail.TypeTagCount + detail.IndividualTagCount;
                result.ClientDistribution.Add(detail);
            }

            _logger.LogInformation($"📊 TAG ANALİZİ:");
            _logger.LogInformation($"   • Aktif device sayısı: {result.DatabaseActiveDevices}");
            _logger.LogInformation($"   • Type tag sayısı: {result.DatabaseTypeTagCount:N0}");
            _logger.LogInformation($"   • Individual tag sayısı: {result.DatabaseIndividualTagCount:N0}");
            _logger.LogInformation($"   • TOPLAM BEKLENEN: {result.DatabaseTotalExpectedTags:N0}");
            _logger.LogInformation($"   • Client sayısı: {result.ClientDistribution.Count}");

            _logger.LogInformation($"📈 EN BÜYÜK DEVICE'LAR:");
            foreach (var device in result.TopDevices.Take(10))
            {
                _logger.LogInformation($"   • Device {device.DeviceId} (Client {device.ClientId}): {device.TotalTags:N0} tag ({device.TypeTagCount:N0}+{device.IndividualTagCount:N0})");
            }

            _logger.LogInformation($"👥 CLIENT DAĞILIMI:");
            foreach (var client in result.ClientDistribution)
            {
                _logger.LogInformation($"   • Client {client.ClientId}: {client.DeviceCount} device, {client.TotalTags:N0} tag");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Tag analizi hatası");
            return new TagAnalysisResult();
        }
    }

    private async Task<ClientAnalysisResult> AnalyzeClientIssuesAsync()
    {
        try
        {
            _logger.LogInformation("👥 Client analizi başlatılıyor...");

            var result = new ClientAnalysisResult();

            // Database'de client'lar
            const string dbClientSql = @"
                SELECT DISTINCT clientId, COUNT(*) as DeviceCount
                FROM channeldevice 
                WHERE clientId IS NOT NULL AND statusCode IN (11,31,41,51,61)
                GROUP BY clientId
                ORDER BY clientId";

            var dbClients = await _dbManager.QueryExchangerAsync<dynamic>(dbClientSql);

            foreach (var client in dbClients)
            {
                result.DatabaseClients.Add(new ClientInfo
                {
                    ClientId = (int)client.clientId,
                    DeviceCount = (int)client.DeviceCount,
                    Source = "Database"
                });
            }

            // Null client'lar
            const string nullClientSql = @"
                SELECT COUNT(*) as NullClientDevices
                FROM channeldevice 
                WHERE clientId IS NULL AND statusCode IN (11,31,41,51,61)";

            var nullCount = await _dbManager.QueryFirstExchangerAsync<int>(nullClientSql);
            result.DevicesWithoutClient = nullCount;

            _logger.LogInformation($"👥 CLIENT ANALİZİ:");
            _logger.LogInformation($"   • Database'de client sayısı: {result.DatabaseClients.Count}");
            _logger.LogInformation($"   • Client'siz device sayısı: {result.DevicesWithoutClient}");

            foreach (var client in result.DatabaseClients)
            {
                _logger.LogInformation($"   • Client {client.ClientId}: {client.DeviceCount} device");
            }

            if (result.DevicesWithoutClient > 0)
            {
                _logger.LogWarning($"⚠️ {result.DevicesWithoutClient} device'ın client'ı yok - bunlar KEP'te görünmeyecek!");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Client analizi hatası");
            return new ClientAnalysisResult();
        }
    }

    private async Task CollectDatabaseMetricsAsync()
    {
        try
        {
            _logger.LogInformation("📊 Veritabanı metrikleri toplanıyor...");

            // Toplam sayılar
            const string totalSql = @"
                SELECT 
                    COUNT(DISTINCT channelName) as TotalChannels,
                    COUNT(*) as TotalDevices,
                    COUNT(DISTINCT deviceTypeId) as TotalDeviceTypes
                FROM channeldevice";

            var totals = await _dbManager.QueryFirstExchangerAsync<dynamic>(totalSql);
            _metrics.Database.TotalChannels = (int)totals.TotalChannels;
            _metrics.Database.TotalDevices = (int)totals.TotalDevices;
            _metrics.Database.TotalDeviceTypes = (int)totals.TotalDeviceTypes;

            // Aktif sayılar
            const string activeSql = @"
                SELECT 
                    COUNT(DISTINCT channelName) as ActiveChannels,
                    COUNT(*) as ActiveDevices
                FROM channeldevice 
                WHERE statusCode IN (11,31,41,61)";

            var actives = await _dbManager.QueryFirstExchangerAsync<dynamic>(activeSql);
            _metrics.Database.ActiveChannels = (int)actives.ActiveChannels;
            _metrics.Database.ActiveDevices = (int)actives.ActiveDevices;

            // Status dağılımı
            const string statusSql = @"
                SELECT statusCode, COUNT(*) as Count
                FROM channeldevice 
                GROUP BY statusCode 
                ORDER BY statusCode";

            var statusResults = await _dbManager.QueryExchangerAsync<dynamic>(statusSql);
            foreach (var status in statusResults)
            {
                _metrics.Database.StatusDistribution[(byte)status.statusCode] = (int)status.Count;
            }

            // Tag sayıları
            const string tagSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM devicetypetag) as TypeTags,
                    (SELECT COUNT(*) FROM deviceindividualtag) as IndividualTags";

            var tags = await _dbManager.QueryFirstExchangerAsync<dynamic>(tagSql);
            _metrics.Database.TotalTypeTags = (int)tags.TypeTags;
            _metrics.Database.TotalIndividualTags = (int)tags.IndividualTags;

            _logger.LogInformation($"📈 Database Metrics:");
            _logger.LogInformation($"   • Total Channels: {_metrics.Database.TotalChannels}");
            _logger.LogInformation($"   • Active Channels: {_metrics.Database.ActiveChannels}");
            _logger.LogInformation($"   • Total Devices: {_metrics.Database.TotalDevices}");
            _logger.LogInformation($"   • Active Devices: {_metrics.Database.ActiveDevices}");
            _logger.LogInformation($"   • Type Tags: {_metrics.Database.TotalTypeTags}");
            _logger.LogInformation($"   • Individual Tags: {_metrics.Database.TotalIndividualTags}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Database metrik toplama hatası");
        }
    }

    private async Task CollectKepServerMetricsAsync()
    {
        try
        {
            _logger.LogInformation("🔍 KEP Server metrikleri toplanıyor...");
            var browseStartTime = DateTime.Now;

            // KEP Server'dan yapıyı oku
            _metrics.KepServer.AllPaths = await GetKepServerChannelsAsync();
            _metrics.KepServer.BrowseDuration = DateTime.Now - browseStartTime;

            // Channel'ları ve device'ları ayır
            foreach (var path in _metrics.KepServer.AllPaths)
            {
                if (path.Contains('.'))
                {
                    // Bu bir device path'i (Channel.Device)
                    var parts = path.Split('.');
                    if (parts.Length == 2)
                    {
                        var channelName = parts[0];
                        _metrics.KepServer.ChannelNames.Add(channelName);
                        _metrics.KepServer.DevicePaths.Add(path);
                    }
                }
                else
                {
                    // Bu sadece channel
                    _metrics.KepServer.ChannelNames.Add(path);
                }
            }

            _logger.LogInformation($"🔍 KEP Server Metrics:");
            _logger.LogInformation($"   • Browse Duration: {_metrics.KepServer.BrowseDuration.TotalMilliseconds:F0}ms");
            _logger.LogInformation($"   • Total Paths: {_metrics.KepServer.AllPaths.Count}");
            _logger.LogInformation($"   • Unique Channels: {_metrics.KepServer.ChannelNames.Count}");
            _logger.LogInformation($"   • Device Paths: {_metrics.KepServer.DevicePaths.Count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 KEP Server metrik toplama hatası");
        }
    }

    private async Task<HashSet<string>> GetKepServerChannelsAsync()
    {
        var validPaths = new HashSet<string>();

        if (_session == null) return validPaths;

        try
        {
            _logger.LogInformation("🔍 KEP Server browse başlatılıyor...");
            var startTime = DateTime.Now;

            // KEP Server restart'tan sonra biraz bekle
            await Task.Delay(5000);

            // Root level browse
            _session.Browse(
                null, null, ObjectIds.ObjectsFolder, 0u,
                BrowseDirection.Forward, ReferenceTypes.HierarchicalReferences,
                true, (uint)NodeClass.Object,
                out var continuationPoint, out var references);

            foreach (var channelRef in references)
            {
                var channelName = channelRef.DisplayName.Text;

                // System channel'larını atla
                if (channelName.StartsWith("_") || channelName == "Server" ||
                    channelName == "Statistics" || channelName == "System")
                    continue;

                // Bu bir channel
                validPaths.Add(channelName);

                try
                {
                    // Channel'ı da browse et
                    var channelNodeId = ExpandedNodeId.ToNodeId(channelRef.NodeId, _session.NamespaceUris);

                    _session.Browse(
                        null, null, channelNodeId, 0u,
                        BrowseDirection.Forward, ReferenceTypes.HierarchicalReferences,
                        true, (uint)NodeClass.Object,
                        out var deviceContinuation, out var deviceReferences);

                    foreach (var deviceRef in deviceReferences)
                    {
                        var deviceName = deviceRef.DisplayName.Text;

                        // Numeric device check
                        if (int.TryParse(deviceName, out _))
                        {
                            var devicePath = $"{channelName}.{deviceName}";
                            validPaths.Add(devicePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"⚠️ Channel browse hatası: {channelName}");
                }
            }

            var browseTime = DateTime.Now - startTime;
            _logger.LogInformation($"✅ KEP Server browse tamamlandı: {validPaths.Count} path, {browseTime.TotalMilliseconds:F0}ms");

            return validPaths;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ KEP Server browse hatası");
            return validPaths;
        }
    }
    private async Task PerformDetailedSyncAsync()
    {
        try
        {
            _logger.LogInformation("🔄 Detaylı senkronizasyon başlatılıyor...");

            // Aktif device'ları al
            var activeDevices = await GetActiveDevicesAsync();
            _metrics.Sync.DatabaseDevices = activeDevices;

            // Expected vs Found karşılaştırması
            var expectedChannels = activeDevices.Select(d => d.ChannelName).Distinct().ToHashSet();
            var expectedDevicePaths = activeDevices.Select(d => $"{d.ChannelName}.{d.DeviceId}").ToHashSet();

            _logger.LogInformation($"📊 KARŞILAŞTIRMA RAPORU:");
            _logger.LogInformation($"   📁 CHANNEL'LAR:");
            _logger.LogInformation($"      • Beklenen: {expectedChannels.Count}");
            _logger.LogInformation($"      • KEP'te bulunan: {_metrics.KepServer.ChannelNames.Count}");

            _logger.LogInformation($"   🔧 DEVICE'LAR:");
            _logger.LogInformation($"      • Beklenen: {expectedDevicePaths.Count}");
            _logger.LogInformation($"      • KEP'te bulunan: {_metrics.KepServer.DevicePaths.Count}");

            // Eksik channel'ları bul
            _metrics.Sync.MissingChannels = expectedChannels
                .Where(expected => !_metrics.KepServer.ChannelNames.Any(kep => kep == expected))
                .ToList();

            // Eksik device'ları bul
            _metrics.Sync.MissingDevices = activeDevices
                .Where(device => !_metrics.KepServer.DevicePaths.Contains($"{device.ChannelName}.{device.DeviceId}"))
                .ToList();

            // Fazladan olanları da bul
            _metrics.Sync.ExtraChannels = _metrics.KepServer.ChannelNames
                .Where(kep => !expectedChannels.Contains(kep))
                .ToList();

            _metrics.Sync.ExtraDevices = _metrics.KepServer.DevicePaths
                .Where(kep => !expectedDevicePaths.Contains(kep))
                .ToList();

            _logger.LogInformation($"🔍 FARK ANALİZİ:");
            _logger.LogInformation($"   ❌ Eksik Channel'lar: {_metrics.Sync.MissingChannels.Count}");
            _logger.LogInformation($"   ❌ Eksik Device'lar: {_metrics.Sync.MissingDevices.Count}");
            _logger.LogInformation($"   ➕ Fazla Channel'lar: {_metrics.Sync.ExtraChannels.Count}");
            _logger.LogInformation($"   ➕ Fazla Device'lar: {_metrics.Sync.ExtraDevices.Count}");

            if (_metrics.Sync.MissingChannels.Any())
            {
                _logger.LogInformation($"📋 Eksik Channel Örnekleri: {string.Join(", ", _metrics.Sync.MissingChannels.Take(5))}");
            }

            if (_metrics.Sync.MissingDevices.Any())
            {
                var examples = _metrics.Sync.MissingDevices.Take(5).Select(d => $"{d.ChannelName}.{d.DeviceId}");
                _logger.LogInformation($"📋 Eksik Device Örnekleri: {string.Join(", ", examples)}");
            }

            // Eksikleri ekle
            if (_metrics.Sync.MissingChannels.Any() || _metrics.Sync.MissingDevices.Any())
            {
                await AddMissingItemsAsync();
            }
            else
            {
                _logger.LogInformation("✅ Hiç eksik item yok - senkronizasyon gerekli değil");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Detaylı senkronizasyon hatası");
        }
    }

    private async Task AddMissingItemsAsync()
    {
        try
        {
            var addStartTime = DateTime.Now;

            // Eksik channel'ları ekle - BATCH VERSION KULLAN
            if (_metrics.Sync.MissingChannels.Any())
            {
                _logger.LogInformation($"🔨 {_metrics.Sync.MissingChannels.Count} eksik channel ekleniyor (BATCH mode)...");
                await AddMissingChannelsBatch(); // 👈 ESKİ: AddMissingChannelsAsync() YENİ: AddMissingChannelsBatch()
            }

            // Eksik device'ları ekle
            if (_metrics.Sync.MissingDevices.Any())
            {
                _logger.LogInformation($"🔨 {_metrics.Sync.MissingDevices.Count} eksik device ekleniyor...");
                await AddMissingDevicesBatch();
            }

            _metrics.Sync.AddDuration = DateTime.Now - addStartTime;

            // Doğrulama yap
            await ValidateAddedItemsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Eksik item ekleme hatası");
        }
    }
    private async Task AddMissingChannelsBatch()
    {
        try
        {
            _logger.LogInformation($"📦 Batch mode ile {_metrics.Sync.MissingChannels.Count} channel ekleniyor...");

            // 10'ar 10'ar grupla
            var batches = _metrics.Sync.MissingChannels
                .Select((channel, index) => new { channel, index })
                .GroupBy(x => x.index / 10)
                .Select(g => g.Select(x => x.channel).ToList());

            int batchNumber = 1;
            foreach (var batch in batches)
            {
                _logger.LogInformation($"🚀 Batch {batchNumber} başlatılıyor ({batch.Count} channel)...");

                var tasks = batch.Select(async channelName =>
                {
                    _logger.LogInformation($"🔨 Channel ekleniyor: {channelName}");

                    // Bu channel'a ait ilk device'ı bul (channel JSON'u için)
                    var sampleDevice = _metrics.Sync.DatabaseDevices.FirstOrDefault(d => d.ChannelName == channelName);
                    if (sampleDevice == null)
                    {
                        _logger.LogError($"❌ Channel {channelName} için örnek device bulunamadı");
                        _metrics.Sync.FailedChannels.Add(channelName);
                        return "FAILED";
                    }

                    var result = await _restApiManager.ChannelPostAsync(sampleDevice.ChannelJson);

                    if (result == "Success")
                    {
                        _logger.LogInformation($"✅ Channel eklendi: {channelName}");
                        _metrics.Sync.AddedChannels.Add(channelName);
                    }
                    else if (result == "Exist")
                    {
                        _logger.LogInformation($"ℹ️ Channel zaten mevcut: {channelName}");
                        _metrics.Sync.AlreadyExistingChannels.Add(channelName);
                    }
                    else
                    {
                        _logger.LogError($"❌ Channel eklenemedi: {channelName}, Result: {result}");
                        _metrics.Sync.FailedChannels.Add(channelName);
                    }

                    return result;
                });

                // Batch'teki tüm task'ler paralel çalışsın
                var results = await Task.WhenAll(tasks);

                _logger.LogInformation($"✅ Batch {batchNumber} tamamlandı - Başarılı: {results.Count(r => r == "Success")}, Mevcut: {results.Count(r => r == "Exist")}, Hatalı: {results.Count(r => r.StartsWith("FAILED"))}");

                // Batch'ler arası bekleme
                if (batchNumber < batches.Count())
                {
                    _logger.LogInformation("⏳ Sonraki batch için 2 saniye bekleniyor...");
                    await Task.Delay(2000);
                }

                batchNumber++;
            }

            _logger.LogInformation($"🎯 Batch işlem tamamlandı - Toplam: {_metrics.Sync.AddedChannels.Count} eklendi, {_metrics.Sync.AlreadyExistingChannels.Count} zaten mevcut, {_metrics.Sync.FailedChannels.Count} hatalı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Batch channel ekleme hatası");
        }
    }
    private async Task AddMissingDevicesBatch()
    {
        try
        {
            _logger.LogInformation($"📦 Batch mode ile {_metrics.Sync.MissingDevices.Count} device ekleniyor...");

            // 5'er 5'er grupla (device'lar daha ağır)
            var batches = _metrics.Sync.MissingDevices
                .Select((device, index) => new { device, index })
                .GroupBy(x => x.index / 5)
                .Select(g => g.Select(x => x.device).ToList());

            int batchNumber = 1;
            foreach (var batch in batches)
            {
                _logger.LogInformation($"🚀 Device Batch {batchNumber} başlatılıyor ({batch.Count} device)...");

                var tasks = batch.Select(async device =>
                {
                    _logger.LogInformation($"🔨 Device ekleniyor: {device.ChannelName}.{device.DeviceId}");

                    var result = await _restApiManager.DevicePostAsync(device.DeviceJson, device.ChannelName);

                    if (result == "Success")
                    {
                        _logger.LogInformation($"✅ Device eklendi: {device.ChannelName}.{device.DeviceId}");
                        _metrics.Sync.AddedDevices.Add(device);

                        // Tag'leri de ekle
                        await AddDeviceTagsAsync(device);
                    }
                    else if (result == "Exist")
                    {
                        _logger.LogInformation($"ℹ️ Device zaten mevcut: {device.ChannelName}.{device.DeviceId}");
                        _metrics.Sync.AlreadyExistingDevices.Add(device);
                    }
                    else
                    {
                        _logger.LogError($"❌ Device eklenemedi: {device.ChannelName}.{device.DeviceId}, Result: {result}");
                        _metrics.Sync.FailedDevices.Add(device);
                    }

                    return result;
                });

                await Task.WhenAll(tasks);

                // Batch'ler arası daha fazla bekleme (device'lar ağır)
                if (batchNumber < batches.Count())
                {
                    _logger.LogInformation("⏳ Sonraki device batch için 3 saniye bekleniyor...");
                    await Task.Delay(3000);
                }

                batchNumber++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Device batch ekleme hatası");
        }
    }
    private async Task AddMissingChannelsAsync()
    {
        try
        {
            foreach (var channelName in _metrics.Sync.MissingChannels)
            {
                _logger.LogInformation($"🔨 Channel ekleniyor: {channelName}");

                // Bu channel'a ait ilk device'ı bul (channel JSON'u için)
                var sampleDevice = _metrics.Sync.DatabaseDevices.FirstOrDefault(d => d.ChannelName == channelName);
                if (sampleDevice == null)
                {
                    _logger.LogError($"❌ Channel {channelName} için örnek device bulunamadı");
                    _metrics.Sync.FailedChannels.Add(channelName);
                    continue;
                }

                var result = await _restApiManager.ChannelPostAsync(sampleDevice.ChannelJson);

                if (result == "Success")
                {
                    _logger.LogInformation($"✅ Channel eklendi: {channelName}");
                    _metrics.Sync.AddedChannels.Add(channelName);
                }
                else if (result == "Exist")
                {
                    _logger.LogInformation($"ℹ️ Channel zaten mevcut: {channelName}");
                    _metrics.Sync.AlreadyExistingChannels.Add(channelName);
                }
                else
                {
                    _logger.LogError($"❌ Channel eklenemedi: {channelName}, Result: {result}");
                    _metrics.Sync.FailedChannels.Add(channelName);
                }

                await Task.Delay(100); // Rate limiting
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Channel ekleme hatası");
        }
    }

    private async Task AddMissingDevicesAsync()
    {
        try
        {
            foreach (var device in _metrics.Sync.MissingDevices)
            {
                _logger.LogInformation($"🔨 Device ekleniyor: {device.ChannelName}.{device.DeviceId}");

                var result = await _restApiManager.DevicePostAsync(device.DeviceJson, device.ChannelName);

                if (result == "Success")
                {
                    _logger.LogInformation($"✅ Device eklendi: {device.ChannelName}.{device.DeviceId}");
                    _metrics.Sync.AddedDevices.Add(device);

                    // Tag'leri de ekle
                    await AddDeviceTagsAsync(device);
                }
                else if (result == "Exist")
                {
                    _logger.LogInformation($"ℹ️ Device zaten mevcut: {device.ChannelName}.{device.DeviceId}");
                    _metrics.Sync.AlreadyExistingDevices.Add(device);
                }
                else
                {
                    _logger.LogError($"❌ Device eklenemedi: {device.ChannelName}.{device.DeviceId}, Result: {result}");
                    _metrics.Sync.FailedDevices.Add(device);
                }

                await Task.Delay(200); // Rate limiting
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Device ekleme hatası");
        }
    }

    private async Task AddDeviceTagsAsync(KepDeviceInfo device)
    {
        try
        {
            // Individual tag'leri ekle
            var individualTagList = await GetDeviceIndividualTagJsonAsync(device.DeviceId);
            if (!string.IsNullOrEmpty(individualTagList) && individualTagList != "[]")
            {
                var tagResult = await _restApiManager.TagPostAsync(individualTagList, device.ChannelName, device.DeviceId.ToString());
                _logger.LogDebug($"Individual tags result for {device.DeviceId}: {tagResult}");
            }

            // Device type tag'leri ekle
            var typeTagList = await GetDeviceTagJsonAsync(device.DeviceId);
            if (!string.IsNullOrEmpty(typeTagList) && typeTagList != "[]")
            {
                var tagResult = await _restApiManager.TagPostAsync(typeTagList, device.ChannelName, device.DeviceId.ToString());
                _logger.LogDebug($"Type tags result for {device.DeviceId}: {tagResult}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Tag ekleme hatası: Device {device.DeviceId}");
        }
    }

    private async Task ValidateAddedItemsAsync()
    {
        try
        {
            _logger.LogInformation("🔍 Eklenen item'lar doğrulanıyor...");
            await Task.Delay(3000); // KEP Server'ın güncellenmesini bekle

            var newKepPaths = await GetKepServerChannelsAsync();
            var stillMissingDevices = 0;
            var stillMissingChannels = 0;

            // Channel doğrulaması
            foreach (var channel in _metrics.Sync.MissingChannels)
            {
                if (!newKepPaths.Any(p => p == channel || p.StartsWith($"{channel}.")))
                {
                    stillMissingChannels++;
                    _logger.LogError($"❌ Channel hala eksik: {channel}");
                }
            }

            // Device doğrulaması
            foreach (var device in _metrics.Sync.MissingDevices)
            {
                var devicePath = $"{device.ChannelName}.{device.DeviceId}";
                if (!newKepPaths.Contains(devicePath))
                {
                    stillMissingDevices++;
                    _logger.LogError($"❌ Device hala eksik: {devicePath}");
                }
            }

            _metrics.Sync.StillMissingChannels = stillMissingChannels;
            _metrics.Sync.StillMissingDevices = stillMissingDevices;

            if (stillMissingChannels == 0 && stillMissingDevices == 0)
            {
                _logger.LogInformation("✅ Tüm eksik item'lar başarıyla eklendi ve doğrulandı");
            }
            else
            {
                _logger.LogWarning($"⚠️ Doğrulama: {stillMissingChannels} channel, {stillMissingDevices} device hala eksik");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Doğrulama hatası");
        }
    }
    private async Task<int> GetMaxTagsPerClientFromDriverAsync()
    {
        try
        {
            const string sql = @"
            SELECT customSettings 
            FROM driver 
            WHERE id = (SELECT DISTINCT driverId FROM channeldevice WHERE driverId IS NOT NULL LIMIT 1)";

            var customSettings = await _dbManager.QueryFirstExchangerAsync<string>(sql);

            if (!string.IsNullOrEmpty(customSettings))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(customSettings);
                if (settings != null && settings.ContainsKey("maxTagsPerClient"))
                {
                    var maxTags = Convert.ToInt32(settings["maxTagsPerClient"]);
                    _logger.LogInformation($"📄 Driver customSettings'den alınan maxTagsPerClient: {maxTags}");
                    return maxTags;
                }
            }

            _logger.LogWarning("Driver customSettings'den maxTagsPerClient okunamadı, default 30000 kullanılıyor");
            return 30000;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driver customSettings'den maxTagsPerClient okunamadı, default 30000 kullanılıyor");
            return 30000;
        }
    }
    private async Task FixMissingClientsAsync(ClientAnalysisResult clientAnalysis)
    {
        try
        {
            // DRIVER JSON'UNDAN AL
            var maxTagsPerClient = await GetMaxTagsPerClientFromDriverAsync();

            var totalTags = await GetTotalActiveTagCountAsync();
            var requiredClients = (int)Math.Ceiling((double)totalTags / maxTagsPerClient);

            _logger.LogInformation($"📊 TAG DAĞITIM ANALİZİ:");
            _logger.LogInformation($"   • Toplam aktif tag: {totalTags:N0}");
            _logger.LogInformation($"   • Driver JSON'dan max tag: {maxTagsPerClient:N0}");
            _logger.LogInformation($"   • Gerekli client sayısı: {requiredClients}");

            if (requiredClients > 14)
            {
                _logger.LogWarning($"⚠️ {requiredClients} client gerekli ama sadece 14 client var!");
            }

            await RedistributeDevicesAsync(Math.Min(requiredClients, 14), maxTagsPerClient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Client düzeltme hatası");
        }
    }
    private async Task RedistributeDevicesAsync(int clientCount, int maxTagsPerClient)
    {
        try
        {
            const string sql = @"
            SELECT 
                cd.id as DeviceId,
                COALESCE(
                    (CASE WHEN dt.allTagJsons IS NOT NULL AND dt.allTagJsons != '[]' 
                     THEN JSON_LENGTH(dt.allTagJsons) ELSE 0 END) +
                    (CASE WHEN cd.individualTags IS NOT NULL AND cd.individualTags != '[]' 
                     THEN JSON_LENGTH(cd.individualTags) ELSE 0 END),
                    0
                ) as TagCount
            FROM channeldevice cd
            LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
            WHERE cd.statusCode IN (11,31,41,51,61)
            ORDER BY TagCount DESC";

            var devices = await _dbManager.QueryExchangerAsync<dynamic>(sql);

            var clientTagCounts = new int[clientCount];
            var clientAssignments = new List<(int DeviceId, int ClientId)>();

            foreach (var device in devices)
            {
                var deviceId = (int)device.DeviceId;
                var tagCount = (int)device.TagCount;

                var targetClientId = Array.IndexOf(clientTagCounts, clientTagCounts.Min()) + 1;

                if (clientTagCounts[targetClientId - 1] + tagCount <= maxTagsPerClient)
                {
                    clientTagCounts[targetClientId - 1] += tagCount;
                    clientAssignments.Add((deviceId, targetClientId));
                }
                else
                {
                    _logger.LogWarning($"⚠️ Device {deviceId} ({tagCount} tag) hiçbir client'a sığmıyor!");
                }
            }

            foreach (var (deviceId, clientId) in clientAssignments)
            {
                const string updateSql = "UPDATE channeldevice SET clientId = @ClientId WHERE id = @DeviceId";
                await _dbManager.ExecuteExchangerAsync(updateSql, new { ClientId = clientId, DeviceId = deviceId });
            }

            _logger.LogInformation($"✅ {clientAssignments.Count} device yeniden dağıtıldı");

            for (int i = 0; i < clientCount; i++)
            {
                _logger.LogInformation($"   • Client {i + 1}: {clientTagCounts[i]:N0} tag");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device redistribution hatası");
        }
    }
    private async Task<int> GetTotalActiveTagCountAsync()
    {
        try
        {
            const string sql = @"
            SELECT 
                SUM(COALESCE(
                    (CASE WHEN dt.allTagJsons IS NOT NULL AND dt.allTagJsons != '[]' 
                     THEN JSON_LENGTH(dt.allTagJsons) ELSE 0 END) +
                    (CASE WHEN cd.individualTags IS NOT NULL AND cd.individualTags != '[]' 
                     THEN JSON_LENGTH(cd.individualTags) ELSE 0 END),
                    0
                )) as TotalTags
            FROM channeldevice cd
            LEFT JOIN devicetype dt ON cd.deviceTypeId = dt.id
            WHERE cd.statusCode IN (11,31,41,51,61)";

            var result = await _dbManager.QueryFirstExchangerAsync<int?>(sql);
            return result ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Total tag count alınamadı");
            return 0;
        }
    }
    private async Task CollectFinalMetricsAsync()
    {
        try
        {
            _logger.LogInformation("📊 Final metrikler toplanıyor...");

            // KEP Server'ı tekrar browse et
            var finalPaths = await GetKepServerChannelsAsync();
            _metrics.Final.TotalPaths = finalPaths.Count;

            foreach (var path in finalPaths)
            {
                if (path.Contains('.') && path.Split('.').Length == 2)
                {
                    _metrics.Final.DevicePaths++;
                    var channelName = path.Split('.')[0];
                    _metrics.Final.UniqueChannels.Add(channelName);
                }
                else
                {
                    _metrics.Final.UniqueChannels.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Final metrik toplama hatası");
        }
    }

    private async Task<List<KepDeviceInfo>> GetActiveDevicesAsync()
    {
        try
        {
            const string sql = @"
                SELECT cd.id AS DeviceId, cd.channelName AS ChannelName, 
                       cd.deviceJson AS DeviceJson, cd.channelJson AS ChannelJson,
                       cd.statusCode AS StatusCode, cd.clientId AS ClientId
                FROM channeldevice cd 
                WHERE cd.statusCode IN (11,31,41,51,61)
                ORDER BY cd.id";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql);

            return results.Select(r => new KepDeviceInfo
            {
                DeviceId = (int)r.DeviceId,
                ChannelName = r.ChannelName ?? "",
                DeviceJson = r.DeviceJson ?? "{}",
                ChannelJson = r.ChannelJson ?? "{}",
                StatusCode = (int)r.StatusCode,
                ClientId = r.ClientId ?? 0
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Aktif device'lar okunamadı");
            return new List<KepDeviceInfo>();
        }
    }

    private async Task<string> GetDeviceTagJsonAsync(int deviceId)
    {
        try
        {
            const string sql = "CALL sp_getDeviceTagjSons(@p_deviceId)";
            var result = await _dbManager.QueryFirstExchangerAsync<string>(sql, new { p_deviceId = deviceId });
            return result ?? "[]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device tag JSON alınamadı: {deviceId}");
            return "[]";
        }
    }

    private async Task<string> GetDeviceIndividualTagJsonAsync(int deviceId)
    {
        try
        {
            const string sql = "CALL sp_getDeviceIndividualTagJsons(@p_deviceId)";
            var result = await _dbManager.QueryFirstExchangerAsync<string>(sql, new { p_deviceId = deviceId });
            return result ?? "[]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device individual tag JSON alınamadı: {deviceId}");
            return "[]";
        }
    }

    private void LogFinalReport()
    {
        _logger.LogInformation("");
        _logger.LogInformation("🎯 ================================");
        _logger.LogInformation("🎯 SYNC RAPORU - ÖZET");
        _logger.LogInformation("🎯 ================================");

        _logger.LogInformation($"⏱️ SÜRE BİLGİLERİ:");
        _logger.LogInformation($"   • Browse süresi: {_metrics.KepServer.BrowseDuration.TotalMilliseconds:F0}ms");
        _logger.LogInformation($"   • Ekleme süresi: {_metrics.Sync.AddDuration.TotalMilliseconds:F0}ms");
        _logger.LogInformation($"   • Toplam süre: {_metrics.TotalSyncDuration.TotalMilliseconds:F0}ms");

        _logger.LogInformation($"📊 DATABASE:");
        _logger.LogInformation($"   • Toplam Channel: {_metrics.Database.TotalChannels}");
        _logger.LogInformation($"   • Aktif Channel: {_metrics.Database.ActiveChannels}");
        _logger.LogInformation($"   • Toplam Device: {_metrics.Database.TotalDevices}");
        _logger.LogInformation($"   • Aktif Device: {_metrics.Database.ActiveDevices}");
        _logger.LogInformation($"   • Type Tag: {_metrics.Database.TotalTypeTags:N0}");
        _logger.LogInformation($"   • Individual Tag: {_metrics.Database.TotalIndividualTags:N0}");

        _logger.LogInformation($"🔍 KEP SERVER (BAŞLANGIÇ):");
        _logger.LogInformation($"   • Toplam Path: {_metrics.KepServer.AllPaths.Count}");
        _logger.LogInformation($"   • Unique Channel: {_metrics.KepServer.ChannelNames.Count}");
        _logger.LogInformation($"   • Device Path: {_metrics.KepServer.DevicePaths.Count}");

        _logger.LogInformation($"🔄 SENKRONIZASYON SONUÇLARI:");
        _logger.LogInformation($"   • Eksik Channel bulundu: {_metrics.Sync.MissingChannels.Count}");
        _logger.LogInformation($"   • Eksik Device bulundu: {_metrics.Sync.MissingDevices.Count}");
        _logger.LogInformation($"   • Channel eklendi: {_metrics.Sync.AddedChannels.Count}");
        _logger.LogInformation($"   • Device eklendi: {_metrics.Sync.AddedDevices.Count}");
        _logger.LogInformation($"   • Channel zaten mevcut: {_metrics.Sync.AlreadyExistingChannels.Count}");
        _logger.LogInformation($"   • Device zaten mevcut: {_metrics.Sync.AlreadyExistingDevices.Count}");
        _logger.LogInformation($"   • Channel ekleme hatası: {_metrics.Sync.FailedChannels.Count}");
        _logger.LogInformation($"   • Device ekleme hatası: {_metrics.Sync.FailedDevices.Count}");

        _logger.LogInformation($"✅ KEP SERVER (FİNAL):");
        _logger.LogInformation($"   • Toplam Path: {_metrics.Final.TotalPaths}");
        _logger.LogInformation($"   • Unique Channel: {_metrics.Final.UniqueChannels.Count}");
        _logger.LogInformation($"   • Device Path: {_metrics.Final.DevicePaths}");

        if (_metrics.Sync.StillMissingChannels > 0 || _metrics.Sync.StillMissingDevices > 0)
        {
            _logger.LogWarning($"⚠️ HALA EKSİK OLANLAR:");
            _logger.LogWarning($"   • Eksik channel: {_metrics.Sync.StillMissingChannels}");
            _logger.LogWarning($"   • Eksik device: {_metrics.Sync.StillMissingDevices}");
        }

        // Status dağılımı
        if (_metrics.Database.StatusDistribution.Any())
        {
            _logger.LogInformation($"📈 STATUS DAĞILIMI:");
            foreach (var status in _metrics.Database.StatusDistribution.OrderBy(x => x.Key))
            {
                _logger.LogInformation($"   • Status {status.Key}: {status.Value} device");
            }
        }

        _logger.LogInformation("🎯 ================================");
        _logger.LogInformation("");
    }

    private void LogTagDiscrepancy(TagAnalysisResult tagAnalysis)
    {
        _logger.LogInformation("");
        _logger.LogInformation("🏷️ ================================");
        _logger.LogInformation("🏷️ TAG UYUŞMAZLIK ANALİZİ");
        _logger.LogInformation("🏷️ ================================");

        _logger.LogInformation($"📊 BEKLENEN vs GERÇEK:");
        _logger.LogInformation($"   • Database'de beklenen: {tagAnalysis.DatabaseTotalExpectedTags:N0} tag");
        _logger.LogInformation($"   • KEP Server'da görülen: 125,000 tag (sizin belirttiğiniz)");
        _logger.LogInformation($"   • Fark: {tagAnalysis.DatabaseTotalExpectedTags - 125000:N0} tag eksik");

        if (tagAnalysis.DatabaseTotalExpectedTags > 125000)
        {
            var missingPercentage = ((double)(tagAnalysis.DatabaseTotalExpectedTags - 125000) / tagAnalysis.DatabaseTotalExpectedTags) * 100;
            _logger.LogWarning($"⚠️ %{missingPercentage:F1} tag eksik!");

            _logger.LogInformation($"🔍 MUHTEMEL SEBEPLER:");
            _logger.LogInformation($"   1. Bazı device'lar KEP Server'a eklenmemiş");
            _logger.LogInformation($"   2. Tag JSON'ları hatalı veya boş");
            _logger.LogInformation($"   3. KEP Server API timeout'ları");
            _logger.LogInformation($"   4. Client'siz device'lar ({tagAnalysis.DatabaseActiveDevices - tagAnalysis.ClientDistribution.Sum(c => c.DeviceCount)} device)");
        }

        _logger.LogInformation("🏷️ ================================");
        _logger.LogInformation("");
    }

    public void Dispose()
    {
        _session?.Close();
        _session?.Dispose();
    }

    public DriverInfo? GetDriverInfo() => _driverInfo;
}

// Metrikler için model sınıfları
public class SyncMetrics
{
    public DatabaseMetrics Database { get; set; } = new();
    public KepServerMetrics KepServer { get; set; } = new();
    public SyncProcessMetrics Sync { get; set; } = new();
    public FinalMetrics Final { get; set; } = new();
    public TimeSpan TotalSyncDuration { get; set; }
}

public class DatabaseMetrics
{
    public int TotalChannels { get; set; }
    public int ActiveChannels { get; set; }
    public int TotalDevices { get; set; }
    public int ActiveDevices { get; set; }
    public int TotalDeviceTypes { get; set; }
    public int TotalTypeTags { get; set; }
    public int TotalIndividualTags { get; set; }
    public Dictionary<byte, int> StatusDistribution { get; set; } = new();
}

public class KepServerMetrics
{
    public HashSet<string> AllPaths { get; set; } = new();
    public HashSet<string> ChannelNames { get; set; } = new();
    public HashSet<string> DevicePaths { get; set; } = new();
    public TimeSpan BrowseDuration { get; set; }
}

public class SyncProcessMetrics
{
    public List<KepDeviceInfo> DatabaseDevices { get; set; } = new();
    public List<string> MissingChannels { get; set; } = new();
    public List<KepDeviceInfo> MissingDevices { get; set; } = new();
    public List<string> ExtraChannels { get; set; } = new();
    public List<string> ExtraDevices { get; set; } = new();

    public List<string> AddedChannels { get; set; } = new();
    public List<KepDeviceInfo> AddedDevices { get; set; } = new();
    public List<string> AlreadyExistingChannels { get; set; } = new();
    public List<KepDeviceInfo> AlreadyExistingDevices { get; set; } = new();
    public List<string> FailedChannels { get; set; } = new();
    public List<KepDeviceInfo> FailedDevices { get; set; } = new();

    public int StillMissingChannels { get; set; }
    public int StillMissingDevices { get; set; }
    public int ClientCount { get; set; }
    public int DevicesPerClient { get; set; }
    public TimeSpan AddDuration { get; set; }
}

public class FinalMetrics
{
    public int TotalPaths { get; set; }
    public HashSet<string> UniqueChannels { get; set; } = new();
    public int DevicePaths { get; set; }
}

// Tag analizi için model sınıfları
public class TagAnalysisResult
{
    public int DatabaseActiveDevices { get; set; }
    public long DatabaseTypeTagCount { get; set; }
    public long DatabaseIndividualTagCount { get; set; }
    public long DatabaseTotalExpectedTags { get; set; }
    public List<DeviceTagDetail> TopDevices { get; set; } = new();
    public List<ClientTagDetail> ClientDistribution { get; set; } = new();
}

public class DeviceTagDetail
{
    public int DeviceId { get; set; }
    public string ChannelName { get; set; } = "";
    public int ClientId { get; set; }
    public byte StatusCode { get; set; }
    public int TypeTagCount { get; set; }
    public int IndividualTagCount { get; set; }
    public int TotalTags { get; set; }
}

public class ClientTagDetail
{
    public int ClientId { get; set; }
    public int DeviceCount { get; set; }
    public long TypeTagCount { get; set; }
    public long IndividualTagCount { get; set; }
    public long TotalTags { get; set; }
}

// Client analizi için model sınıfları
public class ClientAnalysisResult
{
    public List<ClientInfo> DatabaseClients { get; set; } = new();
    public List<ClientInfo> KepServerClients { get; set; } = new();
    public int DevicesWithoutClient { get; set; }
}

public class ClientInfo
{
    public int ClientId { get; set; }
    public int DeviceCount { get; set; }
    public string Source { get; set; } = "";
    public long TagCount { get; set; }
}