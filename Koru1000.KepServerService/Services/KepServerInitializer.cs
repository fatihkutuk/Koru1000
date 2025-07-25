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
    private readonly IKepRestApiManager _restApiManager; // Ekleyin
    private Session? _session;
    private ApplicationConfiguration? _appConfig;
    private DriverInfo? _driverInfo;

    public KepServerInitializer(
        KepServiceConfig config,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        IKepRestApiManager restApiManager, // Ekleyin
        ILogger<KepServerInitializer> logger)
    {
        _config = config;
        _dbManager = dbManager;
        _restApiManager = restApiManager; // Ekleyin
        _logger = logger;
    }
    public async Task<bool> InitializeKepServerAsync()
    {
        try
        {
            _logger.LogInformation("KEP Server başlatılıyor...");

            // Driver ayarlarını veritabanından yükle
            if (!await LoadDriverSettingsAsync())
            {
                _logger.LogError("Driver ayarları yüklenemedi");
                return false;
            }

            if (_config.AutoRestartKepServer)
            {
                if (!await RestartKepServerServiceAsync())
                {
                    _logger.LogError("KEP Server servisi başlatılamadı");
                    return false;
                }

                _logger.LogInformation($"KEP Server restart delay: {_config.KepServerRestartDelay}ms");
                await Task.Delay(_config.KepServerRestartDelay);
            }

            if (!await CreateOpcSessionAsync())
            {
                _logger.LogError("OPC UA Session oluşturulamadı");
                return false;
            }

            if (!await SyncServerConfigurationAsync())
            {
                _logger.LogError("Server konfigürasyonu senkronize edilemedi");
                return false;
            }

            _logger.LogInformation("KEP Server başarıyla başlatıldı");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Server başlatılamadı");
            return false;
        }
    }

    private async Task<bool> LoadDriverSettingsAsync()
    {
        try
        {
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
                _logger.LogError("KEPSERVEREX driver bulunamadı");
                return false;
            }

            _driverInfo = new DriverInfo
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

                    _logger.LogInformation($"Driver ayarları yüklendi - Endpoint: {_driverInfo.CustomSettings.EndpointUrl}, Security: {_driverInfo.CustomSettings.Security.Mode}");
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "CustomSettings JSON parse hatası, default ayarlar kullanılıyor");
                    _driverInfo.CustomSettings = new DriverCustomSettings();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driver ayarları yüklenirken hata");
            return false;
        }
    }

    private async Task<bool> CreateOpcSessionAsync()
    {
        try
        {
            if (_driverInfo?.CustomSettings == null)
            {
                _logger.LogError("Driver ayarları yüklenmemiş");
                return false;
            }

            await CreateApplicationConfigurationAsync();

            var endpointUrl = _driverInfo.CustomSettings.EndpointUrl;
            _logger.LogInformation($"OPC UA bağlantısı kuruluyor: {endpointUrl}");
            _logger.LogInformation($"Security Mode: {_driverInfo.CustomSettings.Security.Mode}");
            _logger.LogInformation($"User: {_driverInfo.CustomSettings.Credentials.Username}");

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
                _logger.LogInformation($"Username authentication kullanılıyor: {_driverInfo.CustomSettings.Credentials.Username}");
            }
            else
            {
                userIdentity = new UserIdentity();
                _logger.LogInformation("Anonymous authentication kullanılıyor");
            }

            _session = await Session.Create(
                _appConfig,
                endpoint,
                false,
                "Koru1000 KEP Server Init Session",
                (uint)_driverInfo.CustomSettings.ConnectionSettings.SessionTimeout,
                userIdentity,
                null);

            _logger.LogInformation("OPC UA Session başarıyla oluşturuldu");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA Session oluşturulamadı");
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

    // RestartKepServerServiceAsync metodunu aynı bırakın...
    public async Task<bool> RestartKepServerServiceAsync()
    {
        try
        {
            _logger.LogInformation($"KEP Server servisi yeniden başlatılıyor: {_config.KepServerServiceName}");

            using var service = new ServiceController(_config.KepServerServiceName);

            var timeout = TimeSpan.FromMinutes(2);

            switch (service.Status)
            {
                case ServiceControllerStatus.Running:
                    _logger.LogInformation("Servis durduruluyor...");
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    break;

                case ServiceControllerStatus.StopPending:
                    _logger.LogInformation("Servisin durması bekleniyor...");
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    break;
            }

            if (service.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("Servis başlatılıyor...");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }

            var isRunning = service.Status == ServiceControllerStatus.Running;
            _logger.LogInformation($"KEP Server servisi durumu: {service.Status}");

            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"KEP Server servisi yeniden başlatılamadı: {_config.KepServerServiceName}");
            return false;
        }
    }

    // Diğer metodları aynı bırakın...
    public async Task<bool> SyncServerConfigurationAsync()
    {
        try
        {
            _logger.LogInformation("Server konfigürasyonu senkronize ediliyor...");

            // KEP Server'dan mevcut yapıyı oku
            var kepChannels = await GetKepServerChannelsAsync();
            _logger.LogInformation($"KEP Server'da {kepChannels.Count} channel bulundu");

            // Veritabanından aktif device'ları al
            var activeDevices = await GetActiveDevicesAsync();
            _logger.LogInformation($"Veritabanında {activeDevices.Count} aktif device bulundu");

            // Eksik channel'ları ekle
            await SyncChannelsAsync(kepChannels, activeDevices);

            // Eksik device'ları ekle  
            await SyncDevicesAsync(kepChannels, activeDevices);

            // Client'lara device'ları dağıt
            await DistributeDevicesToClientsAsync(activeDevices);

            _logger.LogInformation("Server konfigürasyonu başarıyla senkronize edildi");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server konfigürasyonu senkronize edilemedi");
            return false;
        }
    }
    private async Task SyncChannelsAsync(HashSet<string> kepChannels, List<KepDeviceInfo> activeDevices)
    {
        try
        {
            var requiredChannels = activeDevices.Select(d => d.ChannelName).Distinct();

            foreach (var channelName in requiredChannels)
            {
                if (!kepChannels.Any(c => c.StartsWith(channelName)))
                {
                    _logger.LogInformation($"Channel eksik: {channelName} - REST API ile eklenecek");

                    var device = activeDevices.First(d => d.ChannelName == channelName);
                    var result = await _restApiManager.ChannelPostAsync(device.ChannelJson);

                    if (result == "Success" || result == "Exist")
                    {
                        _logger.LogInformation($"Channel eklendi: {channelName}");
                    }
                    else
                    {
                        _logger.LogError($"Channel eklenemedi: {channelName}, Result: {result}");
                    }

                    await Task.Delay(100); // API rate limiting için
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel sync hatası");
        }
    }
    private async Task SyncDevicesAsync(HashSet<string> kepChannels, List<KepDeviceInfo> activeDevices)
    {
        try
        {
            foreach (var device in activeDevices)
            {
                var devicePath = $"{device.ChannelName}.{device.DeviceId}";

                if (!kepChannels.Contains(devicePath))
                {
                    _logger.LogInformation($"Device eksik: {devicePath} - REST API ile eklenecek");

                    var result = await _restApiManager.DevicePostAsync(device.DeviceJson, device.ChannelName);

                    if (result == "Success" || result == "Exist")
                    {
                        // Individual tag'leri ekle
                        var individualTagList = await GetDeviceIndividualTagJsonAsync(device.DeviceId);
                        if (!string.IsNullOrEmpty(individualTagList))
                        {
                            await _restApiManager.TagPostAsync(individualTagList, device.ChannelName, device.DeviceId.ToString());
                        }

                        // Device type tag'leri ekle
                        var tagJsonList = await GetDeviceTagJsonAsync(device.DeviceId);
                        if (!string.IsNullOrEmpty(tagJsonList))
                        {
                            await _restApiManager.TagPostAsync(tagJsonList, device.ChannelName, device.DeviceId.ToString());
                        }

                        _logger.LogInformation($"Device eklendi: {devicePath}");
                    }
                    else
                    {
                        _logger.LogError($"Device eklenemedi: {devicePath}, Result: {result}");
                    }

                    await Task.Delay(200); // API rate limiting için
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device sync hatası");
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
    private async Task DistributeDevicesToClientsAsync(List<KepDeviceInfo> activeDevices)
    {
        try
        {
            // Driver ayarlarından değil, sabit 80 device per client
            var devicesPerClient = 80; // Sabit değer
            var clientCount = (int)Math.Ceiling((double)activeDevices.Count / devicesPerClient);

            _logger.LogInformation($"Device'lar {clientCount} client'a dağıtılıyor (Client başına max {devicesPerClient} device)");

            for (int clientId = 1; clientId <= clientCount; clientId++)
            {
                var clientDevices = activeDevices
                    .Skip((clientId - 1) * devicesPerClient)
                    .Take(devicesPerClient)
                    .Select(d => d.DeviceId.ToString())
                    .ToList();

                if (clientDevices.Any())
                {
                    const string sql = "CALL sp_setClientIdToDevices(@ClientId, @DeviceIds)";
                    await _dbManager.ExecuteExchangerAsync(sql, new
                    {
                        ClientId = clientId,
                        DeviceIds = string.Join(",", clientDevices)
                    });

                    _logger.LogInformation($"Client {clientId}: {clientDevices.Count} device atandı");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device'lar client'lara dağıtılamadı");
        }
    }

    // Diğer helper metodları aynı bırakın...
    private async Task<HashSet<string>> GetKepServerChannelsAsync()
    {
        var channels = new HashSet<string>();

        if (_session == null) return channels;

        try
        {
            _session.Browse(
                null,
                null,
                ObjectIds.ObjectsFolder,
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                out var continuationPoint,
                out var references);

            foreach (var reference in references)
            {
                if (!reference.DisplayName.Text.StartsWith("_") &&
                    reference.DisplayName.Text != "Server")
                {
                    channels.Add(reference.NodeId.Identifier.ToString());

                    // Alt düzey browse et (device'lar için)
                    await BrowseChildNodesAsync(reference.NodeId.ToString(), channels);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KEP Server channels okunamadı");
        }

        return channels;
    }

    private async Task BrowseChildNodesAsync(string nodeId, HashSet<string> channels)
    {
        try
        {
            if (_session == null) return;

            _session.Browse(
                null,
                null,
                new NodeId(nodeId),
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                out var continuationPoint,
                out var references);

            foreach (var reference in references)
            {
                var identifier = reference.NodeId.Identifier.ToString();
                channels.Add(identifier);

                // Sadece 2 seviye derinlik (Channel.Device formatı)
                if (identifier.Split('.').Length < 3)
                {
                    await BrowseChildNodesAsync(reference.NodeId.ToString(), channels);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Child nodes okunamadı: {nodeId}");
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
            _logger.LogError(ex, "Aktif device'lar okunamadı");
            return new List<KepDeviceInfo>();
        }
    }

    public void Dispose()
    {
        _session?.Close();
        _session?.Dispose();
    }

    // Driver ayarlarını alma metodu - diğer servislerde de kullanılabilir
    public DriverInfo? GetDriverInfo() => _driverInfo;
}