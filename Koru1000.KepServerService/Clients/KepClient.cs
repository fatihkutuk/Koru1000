using System.Collections.Concurrent;
using System.Text.Json;
using Koru1000.KepServerService.Models;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Koru1000.KepServerService.Clients;

public class KepClient : IDisposable
{
    private readonly int _clientId;
    private readonly int _driverId;
    private readonly KepServiceConfig _config;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly ILogger<KepClient> _logger;

    private Session? _session;
    private Subscription? _subscription;
    private ApplicationConfiguration? _appConfig;
    private readonly ConcurrentDictionary<uint, KepTagInfo> _monitoredItems;
    private readonly Timer _statusTimer;

    private bool _canProcess = false;
    private long _totalMessagesReceived = 0;
    private long _totalMessagesProcessed = 0;
    private DateTime _lastDataReceived = DateTime.MinValue;
    private KepConnectionStatus _connectionStatus = KepConnectionStatus.Disconnected;
    private string _lastError = "";

    public event EventHandler<KepDataChangedEventArgs>? DataChanged;
    public event EventHandler<KepStatusChangedEventArgs>? StatusChanged;

    public KepClient(
        int clientId,
        KepServiceConfig config,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        int driverId,
        ILogger<KepClient> logger)
    {
        _clientId = clientId;
        _driverId = driverId;
        _config = config;
        _dbManager = dbManager;
        _logger = logger;

        _monitoredItems = new ConcurrentDictionary<uint, KepTagInfo>();
        _statusTimer = new Timer(ReportStatus, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<bool> StartAsync()
    {
        try
        {
            _logger.LogInformation($"🚀 Client {_clientId} (Driver: {_driverId}) başlatılıyor...");

            await CreateApplicationConfigurationAsync();
            await CreateOpcSessionAsync();
            await CreateSubscriptionAsync();
            await LoadAndSubscribeDeviceTagsAsync();

            _canProcess = true;
            _connectionStatus = KepConnectionStatus.Connected;

            _logger.LogInformation($"✅ Client {_clientId} başarıyla başlatıldı");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"💥 Client {_clientId} başlatılamadı");
            _connectionStatus = KepConnectionStatus.Error;
            _lastError = ex.Message;
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _logger.LogInformation($"🛑 Client {_clientId} durduruluyor...");

            _canProcess = false;
            _statusTimer?.Dispose();

            if (_subscription != null)
            {
                try
                {
                    _subscription.Delete(true);
                    _subscription.Dispose();
                }
                catch { }
            }

            if (_session != null)
            {
                try
                {
                    _session.Close();
                    _session.Dispose();
                }
                catch { }
            }

            _connectionStatus = KepConnectionStatus.Disconnected;
            _logger.LogInformation($"✅ Client {_clientId} durduruldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} durdurulamadı");
        }
    }

    public async Task<bool> WriteTagAsync(string nodeId, object value)
    {
        try
        {
            if (_session?.Connected != true)
            {
                _logger.LogWarning($"⚠️ Client {_clientId} - Session bağlı değil");
                return false;
            }

            var nodeToWrite = new NodeId(nodeId);
            var valueToWrite = new DataValue(new Variant(value));
            var nodesToWrite = new WriteValueCollection {
                new WriteValue {
                    NodeId = nodeToWrite,
                    Value = valueToWrite,
                    AttributeId = Attributes.Value
                }
            };

            var response = await _session.WriteAsync(null, nodesToWrite, CancellationToken.None);
            var success = StatusCode.IsGood(response.Results[0]);

            if (success)
            {
                _logger.LogDebug($"✅ Tag yazıldı: {nodeId} = {value}");
            }
            else
            {
                _logger.LogWarning($"⚠️ Tag yazılamadı: {nodeId} = {value}, Error: {response.Results[0]}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Tag yazma hatası: {nodeId}");
            return false;
        }
    }

    private async Task LoadAndSubscribeDeviceTagsAsync()
    {
        try
        {
            // Bu client ve driver'a ait cihazları al
            const string sql = @"
                SELECT d.id as DeviceId, d.channelName as ChannelName, 
                       CONCAT(d.id) as DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                       dtt.id as DeviceTagId
                FROM channeldevice d 
                INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
                WHERE d.clientId = @ClientId 
                  AND d.driverId = @DriverId 
                  AND d.statusCode IN (11,31,41,51,61)
                
                UNION ALL
                
                SELECT d.id as DeviceId, d.channelName as ChannelName, 
                       CONCAT(d.id) as DeviceName, 
                       JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                       dit.id as DeviceTagId
                FROM channeldevice d 
                INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
                WHERE d.clientId = @ClientId 
                  AND d.driverId = @DriverId 
                  AND d.statusCode IN (11,31,41,51,61)";

            var tags = await _dbManager.QueryExchangerAsync<KepTagInfo>(sql,
                new { ClientId = _clientId, DriverId = _driverId });

            if (!tags.Any())
            {
                _logger.LogWarning($"⚠️ Client {_clientId} için tag bulunamadı");
                return;
            }

            _logger.LogInformation($"📊 Client {_clientId} için {tags.Count()} tag subscribe ediliyor...");

            // MonitoredItem HashSet oluştur
            var monitoredItems = new HashSet<MonitoredItem>();

            foreach (var tag in tags)
            {
                // Driver'dan namespace al
                var driverSettings = await GetDriverSettingsAsync();
                var namespaceIndex = driverSettings?.Namespace ?? "2";

                var nodeId = $"ns={namespaceIndex};s={tag.ChannelName}.{tag.DeviceName}.{tag.TagName}";

                var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
                {
                    StartNodeId = new NodeId(nodeId),
                    AttributeId = Attributes.Value,
                    MonitoringMode = MonitoringMode.Reporting,
                    SamplingInterval = 1000,
                    QueueSize = 1,
                    DiscardOldest = true,
                    Handle = tag // Tag bilgisini handle'a ata
                };

                monitoredItems.Add(monitoredItem);
            }

            // Monitored items'ı subscription'a ekle
            _subscription.AddItems(monitoredItems);

            // FastDataChangeCallback ata
            _subscription.FastDataChangeCallback = new FastDataChangeNotificationEventHandler(OnFastDataChanged);

            // Session'a subscription'ı ekle
            _session.AddSubscription(_subscription);

            // Subscription'ı oluştur
            _subscription.Create();

            // ClientHandle'ları _monitoredItems'a ekle
            foreach (var item in monitoredItems)
            {
                if (item.Handle is KepTagInfo tagInfo)
                {
                    _monitoredItems.TryAdd(item.ClientHandle, tagInfo);
                }
            }

            _logger.LogInformation($"✅ Client {_clientId}: {monitoredItems.Count} tag başarıyla subscribe edildi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"💥 Client {_clientId} tag subscription hatası");
        }
    }

    private async Task<DriverCustomSettings?> GetDriverSettingsAsync()
    {
        try
        {
            const string sql = @"
                SELECT d.customSettings
                FROM driver d
                INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                WHERE d.id = @DriverId AND dt.name = 'KEPSERVEREX'";

            var result = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = _driverId });
            var driverData = result.FirstOrDefault();

            if (driverData?.customSettings != null)
            {
                return JsonSerializer.Deserialize<DriverCustomSettings>(
                    driverData.customSettings.ToString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} driver ayarları alınamadı");
            return null;
        }
    }

    private async Task CreateApplicationConfigurationAsync()
    {
        _appConfig = new ApplicationConfiguration()
        {
            ApplicationName = $"Koru1000 KEP Client {_clientId}",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:localhost:Koru1000:KepClient{_clientId}",
            ProductUri = "urn:Koru1000:KepClient",

            ServerConfiguration = new ServerConfiguration(),
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier(),
                TrustedIssuerCertificates = new CertificateTrustList(),
                TrustedPeerCertificates = new CertificateTrustList(),
                RejectedCertificateStore = new CertificateStoreIdentifier(),
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024,
            },

            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 120000,
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
                DefaultSessionTimeout = (int)_config.Limits.SessionTimeoutMs,
                WellKnownDiscoveryUrls = new StringCollection(),
                DiscoveryServers = new EndpointDescriptionCollection(),
                MinSubscriptionLifetime = 10000
            },

            DisableHiResClock = true
        };

        _appConfig.CertificateValidator.CertificateValidation += (s, e) => {
            _logger.LogDebug("Certificate validation: Subject='{Subject}' - ACCEPTING", e.Certificate?.Subject);
            e.Accept = true;
        };
    }

    private async Task CreateOpcSessionAsync()
    {
        var endpoint = new ConfiguredEndpoint(null, new EndpointDescription(_config.EndpointUrl));

        var userIdentity = !string.IsNullOrEmpty(_config.Security.Username)
            ? new UserIdentity(_config.Security.Username, _config.Security.Password)
            : new UserIdentity();

        _session = await Session.Create(
            _appConfig,
            endpoint,
            false,
            $"Koru1000 KEP Client {_clientId}",
            (uint)_config.Limits.SessionTimeoutMs,
            userIdentity,
            null);

        _session.KeepAlive += Session_KeepAlive;
        _logger.LogInformation("Client {ClientId} session created successfully", _clientId);
    }

    private async Task CreateSubscriptionAsync()
    {
        _subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = _config.Limits.PublishingIntervalMs,
            MaxNotificationsPerPublish = (uint)_config.Limits.MaxNotificationsPerPublish,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 100,
            Priority = 0
        };

        _logger.LogInformation("Client {ClientId} subscription created", _clientId);
    }

    // FastDataChangeCallback event handler
    private void OnFastDataChanged(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        if (!_canProcess) return;

        try
        {
            foreach (var item in notification.MonitoredItems)
            {
                if (_monitoredItems.TryGetValue(item.ClientHandle, out var tagInfo))
                {
                    _totalMessagesReceived++;
                    _lastDataReceived = DateTime.Now;

                    var eventArgs = new KepDataChangedEventArgs
                    {
                        ClientId = _clientId,
                        DeviceId = tagInfo.DeviceId,
                        DeviceTagId = tagInfo.DeviceTagId,
                        TagName = tagInfo.TagName,
                        Value = item.Value?.Value,
                        Timestamp = item.Value.ServerTimestamp,
                        Quality = item.Value?.StatusCode.ToString() ?? "Unknown"
                    };

                    DataChanged?.Invoke(this, eventArgs);
                    _totalMessagesProcessed++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} data change processing error");
        }
    }

    private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsBad(e.Status))
        {
            _logger.LogWarning($"⚠️ Client {_clientId} - Keep alive failed: {e.Status}");
            _connectionStatus = KepConnectionStatus.Error;
            _lastError = e.Status.ToString();
        }
    }

    private async void ReportStatus(object? state)
    {
        try
        {
            var status = new KepStatusChangedEventArgs
            {
                ClientId = _clientId,
                Status = _connectionStatus,
                LastDataReceived = _lastDataReceived,
                TotalMessagesReceived = _totalMessagesReceived,
                TotalMessagesProcessed = _totalMessagesProcessed,
                LastError = _lastError,
                MonitoredItemCount = _monitoredItems.Count
            };

            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} status reporting error");
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
    }
}