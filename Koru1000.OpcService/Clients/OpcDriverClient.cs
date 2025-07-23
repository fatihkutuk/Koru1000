using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Security.Certificates; // EKLE
using Koru1000.Core.Models.OpcModels;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua.Configuration; // EKLE

namespace Koru1000.OpcService.Clients
{
    public class OpcDriverClient : IDisposable
    {
        private readonly ConcurrentDictionary<string, OpcTagInfo> _tagInfoLookup;


        private readonly int _driverId;
        private readonly OpcDriverInfo _driverInfo;
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly ClientLimits _limits;
        private readonly ILogger<OpcDriverClient> _logger;

        private Session? _session;
        private ApplicationConfiguration? _appConfig;
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions;
        private readonly ConcurrentDictionary<string, MonitoredItem> _monitoredItems;
        private readonly Timer _reconnectTimer;
        private readonly Timer _statusTimer;

        private OpcConnectionStatus _connectionStatus;
        private DateTime _lastConnected;
        private DateTime _lastDataReceived;
        private long _totalMessagesReceived;
        private long _totalMessagesProcessed;
        private string _lastError = string.Empty;

        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public string DriverName => _driverInfo.DriverName;
        public int DriverId => _driverId;
        public OpcConnectionStatus ConnectionStatus => _connectionStatus;

        public OpcDriverClient(
            int driverId,
    OpcDriverInfo driverInfo,
    Koru1000.DatabaseManager.DatabaseManager dbManager,
    ClientLimits limits,
    ILogger<OpcDriverClient> logger)
        {
            _driverId = driverId;
            _driverInfo = driverInfo;
            _dbManager = dbManager;
            _limits = limits;
            _logger = logger;

            _subscriptions = new ConcurrentDictionary<uint, Subscription>();
            _monitoredItems = new ConcurrentDictionary<string, MonitoredItem>();
            _tagInfoLookup = new ConcurrentDictionary<string, OpcTagInfo>(); // EKLE
            _connectionStatus = OpcConnectionStatus.Disconnected;

            // Timers
            _reconnectTimer = new Timer(ReconnectCallback, null, Timeout.Infinite, Timeout.Infinite);
            _statusTimer = new Timer(StatusCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation($"Starting OPC driver client: {_driverInfo.DriverName}");

                // OPC UA Application Configuration
                await CreateApplicationConfigurationAsync();

                // Connect to OPC Server
                await ConnectAsync();

                // Load and subscribe to tags
                await LoadAndSubscribeTagsAsync();

                _logger.LogInformation($"OPC driver client started: {_driverInfo.DriverName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start OPC driver client: {_driverInfo.DriverName}");
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"Stopping OPC driver client: {_driverInfo.DriverName}");

                UpdateConnectionStatus(OpcConnectionStatus.Disconnected, "Stopping client");

                // Stop timers
                _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // Close session
                if (_session != null)
                {
                    foreach (var subscription in _subscriptions.Values)
                    {
                        subscription.Delete(true);
                    }
                    _subscriptions.Clear();
                    _monitoredItems.Clear();

                    _session.Close();
                    _session.Dispose();
                    _session = null;
                }

                _logger.LogInformation($"OPC driver client stopped: {_driverInfo.DriverName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping OPC driver client: {_driverInfo.DriverName}");
            }
        }

        private async Task CreateApplicationConfigurationAsync()
        {
            var certificateSubjectName = $"CN=Koru1000 OPC Client {_driverId}, C=US, S=Arizona, O=OPC Foundation, DC=" + Utils.GetHostName();

            _appConfig = new ApplicationConfiguration()
            {
                ApplicationName = $"Koru1000 OPC Client - {_driverInfo.DriverName}",
                ApplicationUri = $"urn:localhost:Koru1000:OpcClient:{_driverId}",
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
                    OperationTimeout = 15000,
                    MaxStringLength = 1048576,
                    MaxByteStringLength = 1048576,
                    MaxArrayLength = 65535,
                    MaxMessageSize = 4194304,
                    MaxBufferSize = 65535,
                    ChannelLifetime = 300000,
                    SecurityTokenLifetime = 3600000
                },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = _limits.SessionTimeoutMs },
                TraceConfiguration = new TraceConfiguration()
            };

            await _appConfig.Validate(ApplicationType.Client);

            // Sertifika yönetimi - Working example'dan alındı
            await EnsureApplicationCertificateAsync();

            // Certificate validation callback - Tüm sertifikaları kabul et
            _appConfig.CertificateValidator.CertificateValidation += (s, e) => {
                _logger.LogDebug("Certificate validation: Subject='{Subject}' - ACCEPTING", e.Certificate?.Subject);
                e.Accept = true;
            };
        }

        private async Task EnsureApplicationCertificateAsync()
        {
            try
            {
                var applicationInstance = new ApplicationInstance(_appConfig);

                _logger.LogInformation("Checking/creating application certificate...");

                // Working example'dan alınan metod
                bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 240);

                if (!certificateValid)
                {
                    throw new Exception("Application instance certificate invalid!");
                }

                _logger.LogInformation("Application certificate is valid");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create/validate application certificate");
                throw;
            }
        }
        private async Task ConnectAsync()
        {
            try
            {
                UpdateConnectionStatus(OpcConnectionStatus.Connecting, "Connecting to server");

                _logger.LogInformation("Discovering endpoints at {EndpointUrl}...", _driverInfo.EndpointUrl);

                // Güvenlik modunu driver config'inden belirle
                bool useSecurity = _driverInfo.Security.Mode != "None";

                var endpointDescription = CoreClientUtils.SelectEndpoint(_appConfig, _driverInfo.EndpointUrl, useSecurity: useSecurity);
                var endpointConfiguration = EndpointConfiguration.Create(_appConfig);
                endpointConfiguration.OperationTimeout = 15000;
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                _logger.LogInformation("Selected endpoint: SecurityPolicy='{SecurityPolicy}', SecurityMode='{SecurityMode}'",
                    endpointDescription.SecurityPolicyUri, endpointDescription.SecurityMode);

                // User identity oluştur - Working example'dan
                IUserIdentity userIdentity;

                var usernamePolicy = endpointDescription.UserIdentityTokens?.FirstOrDefault(p => p.TokenType == UserTokenType.UserName);

                if (_driverInfo.Security.UserTokenType == "UserName" &&
                    usernamePolicy != null &&
                    !string.IsNullOrEmpty(_driverInfo.Credentials.Username))
                {
                    _logger.LogInformation("Using Username/Password authentication: {Username}", _driverInfo.Credentials.Username);
                    userIdentity = new UserIdentity(_driverInfo.Credentials.Username, _driverInfo.Credentials.Password);
                }
                else
                {
                    _logger.LogInformation("Using Anonymous authentication");
                    userIdentity = new UserIdentity();
                }

                _session = await Session.Create(_appConfig, endpoint, false,
                    $"Koru1000 OPC Session - {_driverInfo.DriverName}",
                    (uint)_limits.SessionTimeoutMs,
                    userIdentity,
                    null);

                if (_session != null)
                {
                    _session.KeepAlive += Session_KeepAlive;
                    _lastConnected = DateTime.Now;
                    UpdateConnectionStatus(OpcConnectionStatus.Connected, "Connected successfully");

                    _logger.LogInformation("Successfully connected to OPC UA Server: {EndpointUrl}", _driverInfo.EndpointUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to OPC server: {_driverInfo.EndpointUrl}");
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);

                // Schedule reconnect
                _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                throw;
            }
        }

        // LoadAndSubscribeTagsAsync metodunda - SINIRI KALDIR
        private async Task LoadAndSubscribeTagsAsync()
        {
            try
            {
                if (_session == null) return;

                var allTags = await LoadDriverTagsAsync();

                if (!allTags.Any())
                {
                    _logger.LogWarning($"No tags found for driver: {_driverInfo.DriverName}");
                    return;
                }

                // İLK TEST - 1000 tag ile başla
                var tags = allTags.Take(1000).ToList(); // 100'den 1000'e çıkar

                _logger.LogInformation($"Found {allTags.Count} total tags, using first {tags.Count} for testing");

                // ... rest of the method
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load and subscribe tags");
                throw;
            }
        }

        private async Task<List<OpcTagInfo>> LoadDriverTagsAsync()
        {
            try
            {
                _logger.LogInformation($"=== LOADING TAGS FOR DRIVER {_driverId} ===");

                // SQL'e DeviceName ekle - CRITICAL FIX
                const string sql = @"
            SELECT 
                dtt.id as TagId,
                cd.id as DeviceId,
                cd.channelName as ChannelName,
                CONCAT('Device_', cd.id) as DeviceName,  -- DEVICE NAME OLUŞTUR
                JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""servermain.TAG_ADDRESS""')) as TagAddress,
                JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""servermain.TAG_DATA_TYPE""')) as DataType,
                JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""servermain.TAG_READ_WRITE_ACCESS""')) as IsWritable
            FROM channeldevice cd
            INNER JOIN devicetype dt ON cd.deviceTypeId = dt.id
            INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = dt.id
            INNER JOIN driver_channeltype_relation dcr ON dt.ChannelTypeId = dcr.channelTypeId
            WHERE dcr.driverId = @DriverId AND cd.statusCode IN (11,31,41,61)
            
            UNION ALL
            
            SELECT 
                dit.id as TagId,
                cd.id as DeviceId,
                cd.channelName as ChannelName,
                CONCAT('Device_', cd.id) as DeviceName,  -- DEVICE NAME OLUŞTUR
                JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) as TagName,
                JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""servermain.TAG_ADDRESS""')) as TagAddress,
                JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""servermain.TAG_DATA_TYPE""')) as DataType,
                JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""servermain.TAG_READ_WRITE_ACCESS""')) as IsWritable
            FROM channeldevice cd
            INNER JOIN devicetype dt ON cd.deviceTypeId = dt.id
            INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = cd.id
            INNER JOIN driver_channeltype_relation dcr ON dt.ChannelTypeId = dcr.channelTypeId
            WHERE dcr.driverId = @DriverId AND cd.statusCode IN (11,31,41,61)
            ORDER BY DeviceId, TagName";

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = _driverId });

                var tags = new List<OpcTagInfo>();
                foreach (var result in results)
                {
                    if (string.IsNullOrEmpty(result.TagName) || string.IsNullOrEmpty(result.TagAddress))
                        continue;

                    // DOĞRU NodeId formatı - eski koddan
                    string nodeId = $"ns=2;s={result.ChannelName}.{result.DeviceName}.{result.TagName}";

                    // Debug: İlk 5 NodeID'yi göster
                    if (tags.Count < 5)
                    {
                        _logger.LogInformation($"Generated NodeID {tags.Count + 1}: {nodeId}");
                    }

                    tags.Add(new OpcTagInfo
                    {
                        TagId = (int)result.TagId,
                        DeviceId = (int)result.DeviceId,
                        ChannelName = result.ChannelName ?? "",
                        TagName = result.TagName ?? "",
                        NodeId = nodeId,
                        DataType = result.DataType ?? "",
                        IsWritable = result.IsWritable != null && result.IsWritable.ToString() != "0",
                        TagAddress = result.TagAddress ?? ""
                    });
                }

                _logger.LogInformation($"Loaded {tags.Count} tags for driver {_driverInfo.DriverName}");
                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load tags for driver {_driverId}");
                return new List<OpcTagInfo>();
            }
        }
        private async Task CreateSubscriptionForTagsAsync(List<OpcTagInfo> tags)
        {
            try
            {
                if (_session == null || !tags.Any()) return;

                var subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingEnabled = true,
                    PublishingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                    KeepAliveCount = 10,
                    LifetimeCount = 100,
                    MaxNotificationsPerPublish = (uint)_limits.MaxNotificationsPerPublish,
                    Priority = 0
                };

                var monitoredItems = new List<MonitoredItem>();

                foreach (var tag in tags)
                {
                    var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = $"{tag.ChannelName}.{tag.DeviceId}.{tag.TagName}",
                        StartNodeId = tag.NodeId,
                        AttributeId = Attributes.Value,
                        MonitoringMode = MonitoringMode.Reporting,
                        SamplingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                        QueueSize = 1,
                        DiscardOldest = true,
                        Handle = tag // TAG BİLGİSİNİ HANDLE'DA SAKLA - Eski kod gibi
                    };

                    monitoredItems.Add(monitoredItem);
                    _monitoredItems.TryAdd(monitoredItem.DisplayName, monitoredItem);
                }

                subscription.AddItems(monitoredItems);

                // FastDataChangeCallback kullan - ESKİ KOD GİBİ
                subscription.FastDataChangeCallback = FastDataChangeNotificationHandler;

                _session.AddSubscription(subscription);
                subscription.Create();

                _subscriptions.TryAdd(subscription.Id, subscription);

                _logger.LogInformation($"Created subscription {subscription.Id} with {monitoredItems.Count} monitored items using FastDataChangeCallback");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create subscription");
                throw;
            }
        }
        private void FastDataChangeNotificationHandler(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
        {
            try
            {
                var tagValues = new List<OpcTagValue>();

                foreach (var item in notification.MonitoredItems)
                {
                    if (item.Value.StatusCode.ToString() == "Good")
                    {
                        try
                        {
                            // Handle'dan tag bilgisini al
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
                                    _logger.LogInformation($"🎯 FAST DATA #{_totalMessagesReceived}: {tagInfo.TagName} = {item.Value.Value} [{item.Value.StatusCode}]");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error processing fast data change item");
                        }
                    }
                }

                if (tagValues.Any())
                {
                    // Batch olarak data changed event'ini fire et
                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverId,
                        DriverName = _driverInfo.DriverName,
                        TagValues = tagValues,
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);

                    // Performance log
                    if (_totalMessagesReceived % 1000 == 0)
                    {
                        _logger.LogInformation($"📊 PERFORMANCE: {_totalMessagesReceived} messages processed, {tagValues.Count} in this batch");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FastDataChangeNotificationHandler");
            }
        }
        private void MonitoredItem_Notification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            try
            {
                if (e.NotificationValue is MonitoredItemNotification notification &&
                    _tagInfoLookup.TryGetValue(monitoredItem.DisplayName, out var tagInfo))
                {
                    _totalMessagesReceived++;
                    _lastDataReceived = DateTime.Now;

                    // DEBUG LOG - İlk 50 mesajı göster
                    if (_totalMessagesReceived <= 50)
                    {
                        _logger.LogInformation($"🎯 DATA CHANGE #{_totalMessagesReceived}: {tagInfo.TagName} = {notification.Value.Value} [{notification.Value.StatusCode}] @{notification.Value.SourceTimestamp:HH:mm:ss}");
                    }

                    var tagValue = new OpcTagValue
                    {
                        DeviceId = tagInfo.DeviceId,
                        TagName = tagInfo.TagName,
                        Value = notification.Value.Value,
                        Quality = notification.Value.StatusCode.ToString(),
                        SourceTimestamp = notification.Value.SourceTimestamp,
                        ServerTimestamp = notification.Value.ServerTimestamp
                    };

                    // Data changed event'ini fire et
                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverId,
                        DriverName = _driverInfo.DriverName,
                        TagValues = new List<OpcTagValue> { tagValue },
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);

                    // Her 100 mesajda bir özet göster
                    if (_totalMessagesReceived % 100 == 0)
                    {
                        _logger.LogInformation($"📊 Messages received: {_totalMessagesReceived}, Active subscriptions: {_subscriptions.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing notification for {monitoredItem.DisplayName}");
            }
        }

        private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
        {
            try
            {
                if (ServiceResult.IsGood(e.Status))
                {
                    if (_connectionStatus != OpcConnectionStatus.Connected)
                    {
                        _lastConnected = DateTime.Now;
                        UpdateConnectionStatus(OpcConnectionStatus.Connected, "Keep alive OK");
                    }
                }
                else
                {
                    _logger.LogWarning($"Keep alive failed for {_driverInfo.DriverName}: {e.Status}");
                    UpdateConnectionStatus(OpcConnectionStatus.Error, $"Keep alive failed: {e.Status}");

                    // Schedule reconnect
                    _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in keep alive for {_driverInfo.DriverName}");
            }
        }
        private void ReconnectCallback(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_connectionStatus == OpcConnectionStatus.Connected) return;

                    _logger.LogInformation($"Attempting to reconnect: {_driverInfo.DriverName}");
                    UpdateConnectionStatus(OpcConnectionStatus.Reconnecting, "Attempting reconnect");

                    await ConnectAsync();
                    await LoadAndSubscribeTagsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Reconnect failed for {_driverInfo.DriverName}");
                    UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);

                    // Schedule next reconnect
                    _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                }
            });
        }

        private void StatusCallback(object? state)
        {
            // Periyodik status kontrolü ve raporlama
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckConnectionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Status check failed for {_driverInfo.DriverName}");
                }
            });
        }

        public async Task CheckConnectionAsync()
        {
            try
            {
                if (_session != null && _session.Connected)
                {
                    // Simple read test
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
                    if (_connectionStatus == OpcConnectionStatus.Connected)
                    {
                        UpdateConnectionStatus(OpcConnectionStatus.Disconnected, "Session disconnected");
                        _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
            }
        }
        public async Task<OpcServiceStatus> GetStatusAsync()
        {
            return await Task.FromResult(new OpcServiceStatus
            {
                DriverId = _driverId,
                DriverName = _driverInfo.DriverName,
                EndpointUrl = _driverInfo.EndpointUrl,
                ConnectionStatus = _connectionStatus,
                LastConnected = _lastConnected,
                LastDataReceived = _lastDataReceived,
                TotalTagsSubscribed = _monitoredItems.Count,
                ActiveSubscriptions = _subscriptions.Count,
                TotalMessagesReceived = _totalMessagesReceived,
                TotalMessagesProcessed = _totalMessagesProcessed,
                LastError = _lastError,
                StatusTimestamp = DateTime.Now
            });
        }

        private void UpdateConnectionStatus(OpcConnectionStatus status, string message)
        {
            if (_connectionStatus != status)
            {
                _connectionStatus = status;
                _lastError = status == OpcConnectionStatus.Error ? message : string.Empty;

                var eventArgs = new OpcStatusChangedEventArgs
                {
                    DriverId = _driverId,
                    DriverName = _driverInfo.DriverName,
                    Status = status,
                    Message = message,
                    Timestamp = DateTime.Now
                };

                StatusChanged?.Invoke(this, eventArgs);
            }
        }

        public void Dispose()
        {
            _reconnectTimer?.Dispose();
            _statusTimer?.Dispose();

            if (_session != null)
            {
                try
                {
                    _session.Close();
                    _session.Dispose();
                }
                catch { }
            }
        }
    }
}