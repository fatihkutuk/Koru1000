// Koru1000.OpcService/Clients/OpcClient.cs  
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Koru1000.Core.Models.OpcModels;
using System.Text;
using System.Globalization;

namespace Koru1000.OpcService.Clients
{
    public class OpcClient : IDisposable
    {
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
        private int clientId;
        private ILogger<OpcDriverManager> logger;

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

        public OpcClient(int clientId, OpcDriverInfo driverInfo, List<OpcTagInfo> tags, DatabaseManager.DatabaseManager dbManager, ClientLimits limits, ILogger<OpcDriverManager> logger)
        {
            this.clientId = clientId;
            _driverInfo = driverInfo;
            _tags = tags;
            _dbManager = dbManager;
            _limits = limits;
            this.logger = logger;
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation($"Starting OPC Client {_clientId} for driver {_driverInfo.DriverName} with {_tags.Count} tags");

                await CreateApplicationConfigurationAsync();
                await CreateSessionAsync();
                await CreateSubscriptionAsync();
                await CreateMonitoredItemsAsync();
                await StartSubscriptionAsync();

                _canProcess = true;
                UpdateConnectionStatus(OpcConnectionStatus.Connected, "Client started successfully");

                _logger.LogInformation($"OPC Client {_clientId} started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start OPC Client {_clientId}");
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"Stopping OPC Client {_clientId}");
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
                _logger.LogInformation($"OPC Client {_clientId} stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping OPC Client {_clientId}");
            }
        }

        private async Task CreateApplicationConfigurationAsync()
        {
            var certificateSubjectName = $"CN=Koru1000 OPC Client {_clientId}, C=US, S=Arizona, O=OPC Foundation, DC=" + Utils.GetHostName();

            _config = new ApplicationConfiguration()
            {
                ApplicationName = $"Koru1000 OPC Client {_clientId} - {_driverInfo.DriverName}",
                ApplicationUri = $"urn:localhost:Koru1000:OpcClient:{_clientId}",
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
                TransportQuotas = new TransportQuotas { OperationTimeout = 600000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = _limits.SessionTimeoutMs }
            };

            await _config.Validate(ApplicationType.Client);

            var applicationInstance = new ApplicationInstance(_config);
            bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 240);
            if (!certificateValid)
            {
                throw new Exception($"Application certificate invalid for client {_clientId}!");
            }

            _config.CertificateValidator.CertificateValidation += (s, e) => {
                _logger.LogDebug("Client {ClientId} Certificate validation: {Subject} - ACCEPTING", _clientId, e.Certificate?.Subject);
                e.Accept = true;
            };
        }

        private async Task CreateSessionAsync()
        {
            var endpointDescription = CoreClientUtils.SelectEndpoint(_config, _driverInfo.EndpointUrl, useSecurity: true);
            var endpointConfiguration = EndpointConfiguration.Create(_config);
            endpointConfiguration.OperationTimeout = 15000;
            var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

            IUserIdentity userIdentity;
            if (!string.IsNullOrEmpty(_driverInfo.Credentials.Username))
            {
                userIdentity = new UserIdentity(_driverInfo.Credentials.Username, _driverInfo.Credentials.Password);
                _logger.LogInformation("Client {ClientId} using username authentication: {Username}", _clientId, _driverInfo.Credentials.Username);
            }
            else
            {
                userIdentity = new UserIdentity();
                _logger.LogInformation("Client {ClientId} using anonymous authentication", _clientId);
            }

            _session = await Session.Create(_config, endpoint, false,
                $"Koru1000 OPC Client {_clientId}",
                (uint)_limits.SessionTimeoutMs,
                userIdentity,
                null);

            _logger.LogInformation("Client {ClientId} session created successfully", _clientId);
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
                        Handle = tag // ESKİ KOD GİBİ - Handle'da tag bilgisini sakla
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

            // ESKİ KOD GİBİ - FastDataChangeCallback kullan
            _subscription.FastDataChangeCallback = new FastDataChangeNotificationEventHandler(OnDataChanged);

            _session.AddSubscription(_subscription);
            _subscription.Create();

            _logger.LogInformation("Client {ClientId} subscription started with {Count} monitored items", _clientId, _monitoredItems.Count);
        }

        // ESKİ KOD GİBİ - FastDataChangeCallback handler
        private void OnDataChanged(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
        {
            try
            {
                if (!_canProcess) return;

                var textForWrite = new StringBuilder();
                textForWrite.Append("CALL sp_setTagValueOnDataChanged(\"");
                int valueCount = 0;

                foreach (var item in notification.MonitoredItems)
                {
                    if (item.Value.StatusCode.ToString() == "Good")
                    {
                        try
                        {
                            var monitoredItem = subscription.FindItemByClientHandle(item.ClientHandle);
                            if (monitoredItem?.Handle is OpcTagInfo tagInfo)
                            {
                                var doubleValue = Convert.ToDouble(item.Value.Value);
                                textForWrite.Append($"({tagInfo.DeviceId},'{tagInfo.TagName}',{doubleValue.ToString("f6", CultureInfo.InvariantCulture)}),");
                                valueCount++;
                                _totalMessagesReceived++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Client {ClientId} error processing data change item", _clientId);
                        }
                    }
                }

                if (valueCount > 0)
                {
                    // Son virgülü kaldır ve stored procedure'u çağır
                    textForWrite.Remove(textForWrite.Length - 1, 1);
                    textForWrite.Append("\")");

                    // Database'e yaz
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _dbManager.ExecuteKbinAsync(textForWrite.ToString());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Client {ClientId} database write failed", _clientId);
                        }
                    });

                    _lastDataReceived = DateTime.Now;

                    // Debug log - İlk 10 batch için
                    if (_totalMessagesReceived <= 100)
                    {
                        _logger.LogInformation("Client {ClientId} processed {Count} values, total: {Total}", _clientId, valueCount, _totalMessagesReceived);
                    }

                    // Performance log - Her 1000 mesajda bir
                    if (_totalMessagesReceived % 1000 == 0)
                    {
                        _logger.LogInformation("Client {ClientId} performance: {Total} total messages", _clientId, _totalMessagesReceived);
                    }

                    // Data changed event fire et
                    var tagValues = new List<OpcTagValue>();
                    // TODO: Gerekirse tag values listesini doldur

                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverInfo.DriverId,
                        DriverName = $"{_driverInfo.DriverName}-Client{_clientId}",
                        TagValues = tagValues,
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);
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