// Koru1000.KepServerService/Clients/OpcDriverClient.cs (Düzeltilmiş)
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Koru1000.Core.Models.OpcModels;
using System.Collections.Concurrent;
using System.Text;
using System.Globalization;

// Explicit using'ler - conflict'leri önlemek için
using OpcSession = Opc.Ua.Client.Session;
using OpcAttributes = Opc.Ua.Attributes;
using OpcTraceConfiguration = Opc.Ua.TraceConfiguration;

namespace Koru1000.KepServerService.Clients
{
    public class OpcDriverClient : IDisposable
    {
        private readonly ConcurrentDictionary<string, OpcTagInfo> _tagInfoLookup;
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions;
        private readonly ConcurrentDictionary<string, MonitoredItem> _monitoredItems;

        private readonly string _clientId;
        private readonly OpcDriverInfo _driverInfo;
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ClientLimits _limits;
        private readonly ILogger<OpcDriverClient> _logger;

        private OpcSession? _session;  // ✅ Explicit type
        private ApplicationConfiguration? _appConfig;  // ✅ _config -> _appConfig
        private readonly Timer _reconnectTimer;
        private readonly Timer _statusTimer;

        private OpcConnectionStatus _connectionStatus;
        private DateTime _lastConnected;
        private DateTime _lastDataReceived;
        private long _totalMessagesReceived;
        private long _totalMessagesProcessed;
        private string _lastError = string.Empty;

        // Multi-tag support için yeni özellikler
        private readonly object _subscriptionLock = new object();
        private volatile bool _isStarted = false;

        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public string ClientId => _clientId;
        public OpcConnectionStatus ConnectionStatus => _connectionStatus;
        public int GetTagCount() => _tagInfoLookup.Count;

        public OpcDriverClient(
            string clientId,
            OpcDriverInfo driverInfo,
            DatabaseManager.DatabaseManager dbManager,
            ClientLimits limits,
            ILogger<OpcDriverClient> logger)
        {
            _clientId = clientId;
            _driverInfo = driverInfo;
            _dbManager = dbManager;
            _limits = limits;
            _logger = logger;

            _tagInfoLookup = new ConcurrentDictionary<string, OpcTagInfo>();
            _subscriptions = new ConcurrentDictionary<uint, Subscription>();
            _monitoredItems = new ConcurrentDictionary<string, MonitoredItem>();
            _connectionStatus = OpcConnectionStatus.Disconnected;

            // Timers
            _reconnectTimer = new Timer(ReconnectCallback, null, Timeout.Infinite, Timeout.Infinite);
            _statusTimer = new Timer(StatusCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        // Multi-tag support için yeni StartAsync metodu
        public async Task StartAsync(List<OpcTagInfo> tags)
        {
            try
            {
                _logger.LogInformation("🚀 Starting KEPServerEX Client: {ClientId} with {TagCount} tags",
                    _clientId, tags.Count);

                // Tag'ları lookup'a ekle
                foreach (var tag in tags)
                {
                    _tagInfoLookup.TryAdd(tag.NodeId, tag);
                }

                // OPC UA Application Configuration
                await CreateApplicationConfigurationAsync();

                // Connect to OPC Server
                await ConnectAsync();

                // Create subscription with all tags
                await CreateSubscriptionWithTagsAsync(tags);

                _isStarted = true;
                _logger.LogInformation("✅ KEPServerEX Client started: {ClientId}", _clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to start KEPServerEX Client: {ClientId}", _clientId);
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                throw;
            }
        }

        private async Task CreateSubscriptionWithTagsAsync(List<OpcTagInfo> tags)
        {
            try
            {
                if (_session == null || !tags.Any()) return;

                lock (_subscriptionLock)
                {
                    // ✅ Mevcut çalışan kod gibi - DefaultSubscription kullan
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
                            AttributeId = OpcAttributes.Value,  // ✅ Explicit namespace
                            MonitoringMode = MonitoringMode.Reporting,
                            SamplingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                            QueueSize = 1,
                            DiscardOldest = true,
                            Handle = tag // Tag bilgisini handle'da sakla
                        };

                        monitoredItems.Add(monitoredItem);
                        _monitoredItems.TryAdd(monitoredItem.DisplayName, monitoredItem);
                    }

                    subscription.AddItems(monitoredItems);

                    // FastDataChangeCallback kullan (mevcut sistem gibi)
                    subscription.FastDataChangeCallback = FastDataChangeNotificationHandler;

                    // ✅ Mevcut çalışan kod gibi
                    _session.AddSubscription(subscription);
                    subscription.Create();

                    _subscriptions.TryAdd(subscription.Id, subscription);

                    _logger.LogInformation("✅ KEPServerEX Subscription created: {ClientId} - {ItemCount} items",
                        _clientId, monitoredItems.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to create subscription: {ClientId}", _clientId);
                throw;
            }
        }

        // Mevcut FastDataChangeCallback'i koru (optimize edilmiş)
        private void FastDataChangeNotificationHandler(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
        {
            try
            {
                if (!_isStarted) return;

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

                                // Debug log - İlk 5 mesajdan sonra sessiz ol
                                if (_totalMessagesReceived <= 5)
                                {
                                    _logger.LogInformation("🎯 KEPServerEX Data #{MessageCount}: {TagName} = {Value}",
                                        _totalMessagesReceived, tagInfo.TagName, item.Value.Value);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing data item: {ClientId}", _clientId);
                        }
                    }
                }

                if (tagValues.Any())
                {
                    // Batch olarak data changed event'ini fire et
                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverInfo.DriverId,
                        DriverName = $"{_driverInfo.DriverName}-{_clientId}",
                        TagValues = tagValues,
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);

                    // Performance log - Her 1000 mesajda bir
                    if (_totalMessagesReceived % 1000 == 0)
                    {
                        _logger.LogInformation("📊 KEPServerEX Performance {ClientId}: {MessageCount} messages, {BatchSize} in batch",
                            _clientId, _totalMessagesReceived, tagValues.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FastDataChangeNotificationHandler: {ClientId}", _clientId);
            }
        }

        // Dynamic Tag Management - API için
        public async Task<bool> AddTagAsync(OpcTagInfo newTag)
        {
            try
            {
                if (!_isStarted || _session == null) return false;

                _logger.LogInformation("➕ Adding tag to client {ClientId}: {TagName}", _clientId, newTag.TagName);

                // Tag'ı lookup'a ekle
                _tagInfoLookup.TryAdd(newTag.NodeId, newTag);

                // Mevcut subscription'a monitored item ekle
                lock (_subscriptionLock)
                {
                    var subscription = _subscriptions.Values.FirstOrDefault();
                    if (subscription != null)
                    {
                        var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                        {
                            DisplayName = $"{newTag.ChannelName}.{newTag.DeviceId}.{newTag.TagName}",
                            StartNodeId = newTag.NodeId,
                            AttributeId = OpcAttributes.Value,  // ✅ Explicit namespace
                            MonitoringMode = MonitoringMode.Reporting,
                            SamplingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                            QueueSize = 1,
                            DiscardOldest = true,
                            Handle = newTag
                        };

                        subscription.AddItems(new List<MonitoredItem> { monitoredItem });
                        subscription.ApplyChanges();

                        _monitoredItems.TryAdd(monitoredItem.DisplayName, monitoredItem);

                        _logger.LogInformation("✅ Tag added to subscription: {ClientId} - {TagName}",
                            _clientId, newTag.TagName);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to add tag: {ClientId} - {TagName}", _clientId, newTag.TagName);
                return false;
            }
        }

        public async Task<bool> UnsubscribeTagAsync(string nodeId)
        {
            try
            {
                if (!_isStarted || _session == null) return false;

                _logger.LogInformation("➖ Removing tag from client {ClientId}: {NodeId}", _clientId, nodeId);

                // Tag'ı lookup'dan çıkar
                _tagInfoLookup.TryRemove(nodeId, out _);

                // Monitored item'ı bul ve kaldır
                lock (_subscriptionLock)
                {
                    var itemToRemove = _monitoredItems.Values.FirstOrDefault(mi => mi.StartNodeId.ToString() == nodeId);
                    if (itemToRemove != null)
                    {
                        var subscription = _subscriptions.Values.FirstOrDefault();
                        if (subscription != null)
                        {
                            subscription.RemoveItems(new List<MonitoredItem> { itemToRemove });
                            subscription.ApplyChanges();

                            _monitoredItems.TryRemove(itemToRemove.DisplayName, out _);

                            _logger.LogInformation("✅ Tag removed from subscription: {ClientId} - {NodeId}",
                                _clientId, nodeId);
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to remove tag: {ClientId} - {NodeId}", _clientId, nodeId);
                return false;
            }
        }

        // ✅ Mevcut çalışan kodu kullan
        private async Task CreateApplicationConfigurationAsync()
        {
            var certificateSubjectName = $"CN=Koru1000 KEPServerEX Client {_clientId}, C=US, S=Arizona, O=OPC Foundation, DC=" + Utils.GetHostName();

            _appConfig = new ApplicationConfiguration()
            {
                ApplicationName = $"Koru1000 KEPServerEX Client - {_clientId}",
                ApplicationUri = $"urn:localhost:Koru1000:KEPServerEX:{_clientId}",
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
                TraceConfiguration = new OpcTraceConfiguration()  // ✅ Explicit namespace
            };

            await _appConfig.Validate(ApplicationType.Client);
            await EnsureApplicationCertificateAsync();

            // ✅ _config -> _appConfig düzeltildi
            _appConfig.CertificateValidator.CertificateValidation += (s, e) => {
                _logger.LogDebug("KEPServerEX Certificate validation {ClientId}: Subject='{Subject}' - ACCEPTING",
                    _clientId, e.Certificate?.Subject);
                e.Accept = true;
            };
        }

        private async Task EnsureApplicationCertificateAsync()
        {
            try
            {
                var applicationInstance = new ApplicationInstance(_appConfig);
                bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 240);

                if (!certificateValid)
                {
                    throw new Exception($"KEPServerEX Application instance certificate invalid for client {_clientId}!");
                }

                _logger.LogInformation("✅ KEPServerEX Application certificate valid: {ClientId}", _clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to create/validate application certificate: {ClientId}", _clientId);
                throw;
            }
        }

        private async Task ConnectAsync()
        {
            try
            {
                UpdateConnectionStatus(OpcConnectionStatus.Connecting, "Connecting to KEPServerEX");

                _logger.LogInformation("🔗 KEPServerEX discovering endpoints: {ClientId} at {EndpointUrl}",
                    _clientId, _driverInfo.EndpointUrl);

                bool useSecurity = _driverInfo.Security.Mode != "None";
                var endpointDescription = CoreClientUtils.SelectEndpoint(_appConfig, _driverInfo.EndpointUrl, useSecurity: useSecurity);
                var endpointConfiguration = EndpointConfiguration.Create(_appConfig);
                endpointConfiguration.OperationTimeout = 15000;
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                _logger.LogInformation("✅ KEPServerEX Endpoint selected {ClientId}: SecurityPolicy='{SecurityPolicy}', SecurityMode='{SecurityMode}'",
                    _clientId, endpointDescription.SecurityPolicyUri, endpointDescription.SecurityMode);

                // User identity - Driver config'den
                IUserIdentity userIdentity;
                if (_driverInfo.Security.UserTokenType == "UserName" && !string.IsNullOrEmpty(_driverInfo.Credentials.Username))
                {
                    _logger.LogInformation("🔐 KEPServerEX Using Username/Password: {ClientId} - {Username}",
                        _clientId, _driverInfo.Credentials.Username);
                    userIdentity = new UserIdentity(_driverInfo.Credentials.Username, _driverInfo.Credentials.Password);
                }
                else
                {
                    _logger.LogInformation("🔓 KEPServerEX Using Anonymous auth: {ClientId}", _clientId);
                    userIdentity = new UserIdentity();
                }

                // ✅ Explicit type
                _session = await OpcSession.Create(_appConfig, endpoint, false,
                    $"Koru1000 KEPServerEX Session - {_clientId}",
                    (uint)_limits.SessionTimeoutMs,
                    userIdentity,
                    null);

                if (_session != null)
                {
                    _session.KeepAlive += Session_KeepAlive;
                    _lastConnected = DateTime.Now;
                    UpdateConnectionStatus(OpcConnectionStatus.Connected, "Connected to KEPServerEX");

                    _logger.LogInformation("✅ KEPServerEX Connected: {ClientId} to {EndpointUrl}",
                        _clientId, _driverInfo.EndpointUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX Connection failed: {ClientId}", _clientId);
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                throw;
            }
        }

        // ✅ Mevcut çalışan kod
        private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
        {
            try
            {
                if (ServiceResult.IsGood(e.Status))
                {
                    if (_connectionStatus != OpcConnectionStatus.Connected)
                    {
                        _lastConnected = DateTime.Now;
                        UpdateConnectionStatus(OpcConnectionStatus.Connected, "KEPServerEX Keep alive OK");
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️ KEPServerEX Keep alive failed {ClientId}: {Status}", _clientId, e.Status);
                    UpdateConnectionStatus(OpcConnectionStatus.Error, $"Keep alive failed: {e.Status}");
                    _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in keep alive: {ClientId}", _clientId);
            }
        }

        private void ReconnectCallback(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_connectionStatus == OpcConnectionStatus.Connected) return;

                    _logger.LogInformation("🔄 KEPServerEX Attempting reconnect: {ClientId}", _clientId);
                    UpdateConnectionStatus(OpcConnectionStatus.Reconnecting, "Attempting reconnect");

                    await ConnectAsync();

                    // Mevcut tag'ları ile subscription'ı yeniden oluştur
                    var currentTags = _tagInfoLookup.Values.ToList();
                    if (currentTags.Any())
                    {
                        await CreateSubscriptionWithTagsAsync(currentTags);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ KEPServerEX Reconnect failed: {ClientId}", _clientId);
                    UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                    _reconnectTimer.Change(TimeSpan.FromMilliseconds(_limits.ReconnectDelayMs), Timeout.InfiniteTimeSpan);
                }
            });
        }

        private void StatusCallback(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckConnectionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Status check failed: {ClientId}", _clientId);
                }
            });
        }

        public async Task CheckConnectionAsync()
        {
            try
            {
                // ✅ Null check ve Connected property kontrolü düzeltildi
                if (_session != null && _session.Connected)
                {
                    var nodesToRead = new ReadValueIdCollection();
                    nodesToRead.Add(new ReadValueId { NodeId = Variables.Server_ServerStatus_CurrentTime, AttributeId = OpcAttributes.Value });

                    // ✅ Session.Read method signature düzeltildi
                    _session.Read(null, 0, TimestampsToReturn.Both, nodesToRead, out var results, out var diagnosticInfos);

                    // ✅ results.Count > 0 kontrolü düzeltildi
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

        private void UpdateConnectionStatus(OpcConnectionStatus status, string message)
        {
            if (_connectionStatus != status)
            {
                _connectionStatus = status;
                _lastError = status == OpcConnectionStatus.Error ? message : string.Empty;

                var eventArgs = new OpcStatusChangedEventArgs
                {
                    DriverId = _driverInfo.DriverId,
                    DriverName = $"{_driverInfo.DriverName}-{_clientId}",
                    Status = status,
                    Message = message,
                    Timestamp = DateTime.Now
                };

                StatusChanged?.Invoke(this, eventArgs);
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation("🛑 Stopping KEPServerEX Client: {ClientId}", _clientId);
                _isStarted = false;

                UpdateConnectionStatus(OpcConnectionStatus.Disconnected, "Client stopping");

                _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

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

                _logger.LogInformation("✅ KEPServerEX Client stopped: {ClientId}", _clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping KEPServerEX Client: {ClientId}", _clientId);
            }
        }

        public void Dispose()
        {
            _isStarted = false;
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