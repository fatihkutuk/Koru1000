// Koru1000.KepServerService/Clients/OpcClient.cs
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Koru1000.Core.Models.OpcModels;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService.Clients
{
    public class OpcClient : IDisposable
    {
        public long TotalMessagesReceived => _totalMessagesReceived;

        private readonly int _clientId;
        private readonly OpcDriverInfo _driverInfo;
        private readonly List<OpcTagInfo> _tags;
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly ClientLimits _limits;
        private readonly ILogger<OpcClient> _logger;

        private ApplicationConfiguration? _config;
        private Session? _session;
        private Subscription? _subscription;
        private readonly HashSet<MonitoredItem> _monitoredItems;

        private bool _canProcess = false;
        private long _totalMessagesReceived = 0;
        private DateTime _lastDataReceived = DateTime.MinValue;
        private OpcConnectionStatus _connectionStatus = OpcConnectionStatus.Disconnected;

        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public int ClientId => _clientId;
        public string DriverName => _driverInfo.DriverName;
        public OpcConnectionStatus ConnectionStatus => _connectionStatus;

        public OpcClient(
            int clientId,
            OpcDriverInfo driverInfo,
            List<OpcTagInfo> tags,
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            ClientLimits limits,
            ILogger<OpcClient> logger)
        {
            _clientId = clientId;
            _driverInfo = driverInfo;
            _tags = tags;
            _dbManager = dbManager;
            _limits = limits;
            _logger = logger;
            _monitoredItems = new HashSet<MonitoredItem>();
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation($"🚀 OPC Client {_clientId} başlatılıyor - Driver: {_driverInfo.DriverName}, Tags: {_tags.Count}");

                await CreateApplicationConfigurationAsync();
                await CreateSessionAsync();
                await CreateSubscriptionAsync();
                await CreateMonitoredItemsAsync();
                await StartSubscriptionAsync();

                _canProcess = true;
                UpdateConnectionStatus(OpcConnectionStatus.Connected, "Client started successfully");

                _logger.LogInformation($"✅ OPC Client {_clientId} başlatıldı - {_tags.Count} tag aktif");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ OPC Client {_clientId} başlatılamadı");
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"🛑 OPC Client {_clientId} durduruluyor...");
                _canProcess = false;

                if (_subscription != null)
                {
                    _subscription.Delete(false);
                    _subscription.Dispose();
                    _subscription = null;
                }

                if (_session != null)
                {
                    _session.Close();
                    _session.Dispose();
                    _session = null;
                }

                UpdateConnectionStatus(OpcConnectionStatus.Disconnected, "Client stopped");
                _logger.LogInformation($"✅ OPC Client {_clientId} durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ OPC Client {_clientId} durdurulurken hata");
            }
        }

        // OpcClient.cs - CreateApplicationConfigurationAsync methodunu tamamen değiştirin
        private async Task CreateApplicationConfigurationAsync()
        {
            // ✅ Driver-level shared certificate
            var driverCertificateName = $"CN=Koru1000 Driver {_driverInfo.DriverId}, C=US, S=Arizona, O=OPC Foundation, DC=" + Utils.GetHostName();

            _config = new ApplicationConfiguration()
            {
                // ✅ Driver seviyesinde shared application identity
                ApplicationName = $"Koru1000 Driver {_driverInfo.DriverId} - {_driverInfo.DriverName}",
                ApplicationUri = $"urn:localhost:Koru1000:Driver:{_driverInfo.DriverId}", // Client ID yok!
                ApplicationType = ApplicationType.Client,

                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = @"Directory",
                        StorePath = @"%LocalApplicationData%\OPC Foundation\pki\own",
                        // ✅ Driver-specific certificate subject
                        SubjectName = driverCertificateName
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
                    OperationTimeout = 15000,
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
                    DefaultSessionTimeout = _limits.SessionTimeoutMs
                },

                TraceConfiguration = new TraceConfiguration()
            };

            // ✅ Certificate validation - Tüm certificate'ları kabul et
            _config.CertificateValidator.CertificateValidation += (s, e) => {
                _logger.LogDebug("Driver {DriverId} Client {ClientId} Certificate AUTO ACCEPTED: {Subject}",
                    _driverInfo.DriverId, _clientId, e.Certificate?.Subject ?? "Unknown");
                e.Accept = true;
            };

            try
            {
                var applicationInstance = new ApplicationInstance(_config);

                // ✅ Certificate check - Sadece ilk client yapsın
                bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 0);

                if (!certificateValid)
                {
                    _logger.LogWarning("Driver {DriverId} Client {ClientId} Certificate invalid, continuing...",
                        _driverInfo.DriverId, _clientId);
                }
                else
                {
                    _logger.LogInformation("Driver {DriverId} Client {ClientId} Using shared certificate: {Subject}",
                        _driverInfo.DriverId, _clientId, driverCertificateName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Driver {DriverId} Client {ClientId} Certificate setup warning, continuing...",
                    _driverInfo.DriverId, _clientId);
            }
        }

        // OpcClient.cs - CreateSessionAsync methodunun tamamı (Retry logic ile)
        // OpcClient.cs - CreateSessionAsync methodunda session name'i değiştirin
        private async Task CreateSessionAsync()
        {
            const int maxRetries = 3;
            const int retryDelayMs = 5000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _logger.LogInformation("Driver {DriverId} Client {ClientId} attempting session creation (attempt {Attempt}/{MaxRetries})",
                        _driverInfo.DriverId, _clientId, attempt, maxRetries);

                    var endpointDescription = CoreClientUtils.SelectEndpoint(_config, _driverInfo.EndpointUrl, useSecurity: true);
                    var endpointConfiguration = EndpointConfiguration.Create(_config);
                    endpointConfiguration.OperationTimeout = 15000;
                    var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                    IUserIdentity userIdentity;
                    if (!string.IsNullOrEmpty(_driverInfo.Credentials.Username))
                    {
                        userIdentity = new UserIdentity(_driverInfo.Credentials.Username, _driverInfo.Credentials.Password);
                    }
                    else
                    {
                        userIdentity = new UserIdentity();
                    }

                    // ✅ Driver-level shared session name (Client ID dahil)
                    var sessionName = $"Koru1000 Driver {_driverInfo.DriverId} Client {_clientId}";

                    _session = await Session.Create(_config, endpoint, false,
                        sessionName, // Shared driver certificate ama unique session name
                        (uint)_limits.SessionTimeoutMs,
                        userIdentity,
                        null);

                    _logger.LogInformation("✅ Driver {DriverId} Client {ClientId} session created: {SessionName} (attempt {Attempt})",
                        _driverInfo.DriverId, _clientId, sessionName, attempt);
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "⚠️ Driver {DriverId} Client {ClientId} session creation failed (attempt {Attempt}/{MaxRetries}), retrying...",
                        _driverInfo.DriverId, _clientId, attempt, maxRetries);

                    await Task.Delay(retryDelayMs);
                }
                catch (Exception ex) when (attempt == maxRetries)
                {
                    _logger.LogError(ex, "❌ Driver {DriverId} Client {ClientId} session creation failed after {MaxRetries} attempts",
                        _driverInfo.DriverId, _clientId, maxRetries);
                    throw;
                }
            }
        }

        private async Task CreateSubscriptionAsync()
        {
            _subscription = new Subscription(_session.DefaultSubscription)
            {
                PublishingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                MaxNotificationsPerPublish = (uint)_limits.MaxNotificationsPerPublish,
                PublishingEnabled = true
            };

            _logger.LogInformation("Client {ClientId} subscription created", _clientId);
        }

        private async Task CreateMonitoredItemsAsync()
        {
            foreach (var tag in _tags)
            {
                try
                {
                    var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
                    {
                        DisplayName = $"{tag.ChannelName}.{tag.DeviceId}.{tag.TagName}",
                        StartNodeId = tag.NodeId,
                        AttributeId = Attributes.Value,
                        MonitoringMode = MonitoringMode.Reporting,
                        SamplingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                        QueueSize = 1,
                        DiscardOldest = true,
                        Handle = tag // Handle'da tag bilgisini sakla
                    };

                    _monitoredItems.Add(monitoredItem);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Client {ClientId} failed to create monitored item for tag: {NodeId}", _clientId, tag.NodeId);
                }
            }

            _logger.LogInformation("Client {ClientId} created {Count} monitored items", _clientId, _monitoredItems.Count);
        }

        private async Task StartSubscriptionAsync()
        {
            _subscription.AddItems(_monitoredItems);

            // FastDataChangeCallback kullan
            _subscription.FastDataChangeCallback = new FastDataChangeNotificationEventHandler(OnDataChanged);

            _session.AddSubscription(_subscription);
            _subscription.Create();

            _logger.LogInformation("Client {ClientId} subscription started with {Count} monitored items", _clientId, _monitoredItems.Count);
        }

        // FastDataChangeCallback handler
        private void OnDataChanged(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
        {
            try
            {
                if (!_canProcess) return;

                var tagValues = new List<OpcTagValue>();

                foreach (var item in notification.MonitoredItems)
                {
                    if (item.Value.StatusCode.ToString() == "Good")
                    {
                        try
                        {
                            var monitoredItem = subscription.FindItemByClientHandle(item.ClientHandle);
                            if (monitoredItem?.Handle is OpcTagInfo tagInfo)
                            {
                                _totalMessagesReceived++;
                                _lastDataReceived = DateTime.Now;

                                var tagValue = new OpcTagValue
                                {
                                    DeviceId = tagInfo.DeviceId,
                                    TagName = tagInfo.TagName,
                                    Value = item.Value.Value,
                                    Quality = item.Value.StatusCode.ToString(),
                                    SourceTimestamp = item.Value.SourceTimestamp,
                                    ServerTimestamp = item.Value.ServerTimestamp
                                };

                                tagValues.Add(tagValue);

                                // Debug log - İlk 10 mesajı göster
                                if (_totalMessagesReceived <= 10)
                                {
                                    _logger.LogInformation($"🎯 CLIENT {_clientId} DATA #{_totalMessagesReceived}: {tagInfo.TagName} = {item.Value.Value} [{item.Value.StatusCode}]");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Client {ClientId} error processing data change item", _clientId);
                        }
                    }
                }

                if (tagValues.Any())
                {
                    // Data changed event fire et
                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverInfo.DriverId,
                        DriverName = $"{_driverInfo.DriverName}-Client{_clientId}",
                        TagValues = tagValues,
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);

                    // Performance log - Her 1000 mesajda bir
                    if (_totalMessagesReceived % 1000 == 0)
                    {
                        _logger.LogInformation("Client {ClientId} performance: {Total} total messages, {BatchSize} in this batch",
                            _clientId, _totalMessagesReceived, tagValues.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client {ClientId} data changed handler error", _clientId);
            }
        }

        public async Task CheckConnectionAsync()
        {
            try
            {
                if (_session?.Connected == true)
                {
                    // Simple test read
                    var nodesToRead = new ReadValueIdCollection();
                    nodesToRead.Add(new ReadValueId { NodeId = Variables.Server_ServerStatus_CurrentTime, AttributeId = Attributes.Value });

                    _session.Read(null, 0, TimestampsToReturn.Both, nodesToRead, out var results, out var diagnosticInfos);

                    if (results != null && results.Count > 0 && StatusCode.IsGood(results[0].StatusCode))
                    {
                        if (_connectionStatus != OpcConnectionStatus.Connected)
                        {
                            UpdateConnectionStatus(OpcConnectionStatus.Connected, "Connection verified");
                        }
                    }
                }
                else
                {
                    UpdateConnectionStatus(OpcConnectionStatus.Disconnected, "Session disconnected");
                }
            }
            catch (Exception ex)
            {
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
            }
        }

        public async Task<OpcServiceStatus> GetStatusAsync()
        {
            return await Task.FromResult(new OpcServiceStatus
            {
                DriverId = _driverInfo.DriverId,
                DriverName = $"{_driverInfo.DriverName}-Client{_clientId}",
                EndpointUrl = _driverInfo.EndpointUrl,
                ConnectionStatus = _connectionStatus,
                LastConnected = DateTime.Now, // TODO: Gerçek değer
                LastDataReceived = _lastDataReceived,
                TotalTagsSubscribed = _tags.Count,
                ActiveSubscriptions = 1,
                TotalMessagesReceived = _totalMessagesReceived,
                TotalMessagesProcessed = _totalMessagesReceived,
                LastError = "",
                StatusTimestamp = DateTime.Now
            });
        }

        private void UpdateConnectionStatus(OpcConnectionStatus status, string message)
        {
            if (_connectionStatus != status)
            {
                _connectionStatus = status;

                var eventArgs = new OpcStatusChangedEventArgs
                {
                    DriverId = _driverInfo.DriverId,
                    DriverName = $"{_driverInfo.DriverName}-Client{_clientId}",
                    Status = status,
                    Message = message,
                    Timestamp = DateTime.Now
                };

                StatusChanged?.Invoke(this, eventArgs);
            }
        }

        public void Dispose()
        {
            _canProcess = false;

            try
            {
                _subscription?.Dispose();
                _session?.Dispose();
            }
            catch { }
        }
    }
}