// Koru1000.OpcService/Clients/OpcClient.cs
using Opc.Ua;
using Opc.Ua.Client;
using Koru1000.Core.Models.OpcModels;
using Koru1000.OpcService.Services;
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
                _logger.LogInformation($"🚀 Starting OPC Client {_clientId} for driver {_driverInfo.DriverName} with {_tags.Count} tags");

                await CreateApplicationConfigurationAsync();
                await CreateSessionAsync();
                await CreateSubscriptionAsync();
                await CreateMonitoredItemsAsync();
                await StartSubscriptionAsync();

                _canProcess = true;
                UpdateConnectionStatus(OpcConnectionStatus.Connected, "Client started successfully");

                _logger.LogInformation($"✅ OPC Client {_clientId} started successfully - Ready to receive data");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to start OPC Client {_clientId}");
                UpdateConnectionStatus(OpcConnectionStatus.Error, ex.Message);
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"🛑 Stopping OPC Client {_clientId}");
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
                _logger.LogInformation($"✅ OPC Client {_clientId} stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error stopping OPC Client {_clientId}");
            }
        }

        private async Task CreateApplicationConfigurationAsync()
        {
            _config = await SharedApplicationConfiguration.GetInstanceAsync(_logger);
            _logger.LogInformation($"🔧 Client {_clientId} using shared application configuration");
        }

        private async Task CreateSessionAsync()
        {
            try
            {
                _logger.LogInformation($"🔌 Client {_clientId} creating session to {_driverInfo.EndpointUrl}");

                bool useSecurity = !string.Equals(_driverInfo.Security.Mode, "None", StringComparison.OrdinalIgnoreCase);

                var endpointDescription = CoreClientUtils.SelectEndpoint(_config, _driverInfo.EndpointUrl, useSecurity: useSecurity);
                var endpointConfiguration = EndpointConfiguration.Create(_config);
                endpointConfiguration.OperationTimeout = 30000;

                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                _logger.LogInformation($"🔐 Client {_clientId} selected endpoint: SecurityPolicy='{endpointDescription.SecurityPolicyUri}', SecurityMode='{endpointDescription.SecurityMode}'");

                IUserIdentity userIdentity;

                if (_driverInfo.Security.UserTokenType == "UserName" &&
                    !string.IsNullOrEmpty(_driverInfo.Credentials.Username))
                {
                    var username = _driverInfo.Credentials.Username.Trim();
                    var password = _driverInfo.Credentials.Password?.Trim() ?? "";

                    userIdentity = new UserIdentity(username, password);
                    _logger.LogInformation($"👤 Client {_clientId} using username authentication: {username}");
                }
                else
                {
                    userIdentity = new UserIdentity();
                    _logger.LogInformation($"🔓 Client {_clientId} using anonymous authentication");
                }

                _session = await Session.Create(_config, endpoint, false,
                    $"Koru1000 OPC Client {_clientId}",
                    (uint)_limits.SessionTimeoutMs,
                    userIdentity,
                    null);

                if (_session == null)
                {
                    throw new Exception($"Session could not be created for client {_clientId}");
                }

                _logger.LogInformation($"✅ Client {_clientId} session created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_clientId} failed to create session");
                throw;
            }
        }

        private async Task CreateSubscriptionAsync()
        {
            try
            {
                _logger.LogInformation($"📋 Client {_clientId} creating subscription");

                _subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingInterval = _driverInfo.ConnectionSettings.PublishingInterval,
                    MaxNotificationsPerPublish = (uint)Math.Min(_limits.MaxNotificationsPerPublish, _tags.Count),
                    PublishingEnabled = true,
                    KeepAliveCount = 10,
                    LifetimeCount = 100,
                    Priority = 0
                };

                _logger.LogInformation($"⚙️ Client {_clientId} subscription created - PublishingInterval={_subscription.PublishingInterval}ms, MaxNotifications={_subscription.MaxNotificationsPerPublish}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_clientId} failed to create subscription");
                throw;
            }
        }

        private async Task CreateMonitoredItemsAsync()
        {
            try
            {
                _logger.LogInformation($"🏷️ Client {_clientId} creating {_tags.Count} monitored items");

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
                            SamplingInterval = Math.Max(_driverInfo.ConnectionSettings.PublishingInterval, 1000),
                            QueueSize = 1,
                            DiscardOldest = true,
                            Handle = tag // ESKİ KOD GİBİ - Handle olarak tag bilgisini sakla
                        };

                        _monitoredItems.Add(monitoredItem);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Client {_clientId} failed to create monitored item for tag: {tag.NodeId}");
                    }
                }

                _logger.LogInformation($"✅ Client {_clientId} created {_monitoredItems.Count} monitored items successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_clientId} failed to create monitored items");
                throw;
            }
        }

        // Koru1000.OpcService/Clients/OpcClient.cs - StartSubscriptionAsync metodunu düzelt
        // Koru1000.OpcService/Clients/OpcClient.cs - StartSubscriptionAsync metodunu tamamen yeniden yaz
        private async Task StartSubscriptionAsync()
        {
            try
            {
                _logger.LogInformation($"🚀 Client {_clientId} starting subscription with {_monitoredItems.Count} items");

                // EXPLICIT TYPE BELIRTME
                var itemsList = new List<MonitoredItem>(_monitoredItems);

                // TEK SEFERDE EKLE
                _subscription.AddItems(itemsList);
                _logger.LogInformation($"➕ Client {_clientId} added all {itemsList.Count} items to subscription");

                // ESKİ KOD GİBİ - FastDataChangeCallback
                _subscription.FastDataChangeCallback = OnDataChanged;
                _logger.LogInformation($"📡 Client {_clientId} FastDataChangeCallback set");

                _session.AddSubscription(_subscription);
                _logger.LogInformation($"📋 Client {_clientId} subscription added to session");

                try
                {
                    _logger.LogInformation($"🔨 Client {_clientId} creating subscription...");
                    _subscription.Create();

                    if (_subscription.Created)
                    {
                        var totalItems = _subscription.MonitoredItems.Count();
                        _logger.LogInformation($"✅ Client {_clientId} subscription CREATED SUCCESSFULLY with {totalItems} monitored items");

                        // STATUS KONTROLÜNÜ ATLA - Sadece sayı ver
                        if (totalItems > 0)
                        {
                            _logger.LogInformation($"📊 Client {_clientId} has {totalItems} monitored items");
                        }
                        else
                        {
                            _logger.LogWarning($"⚠️ Client {_clientId} has NO monitored items!");
                        }
                    }
                    else
                    {
                        _logger.LogError($"❌ Client {_clientId} subscription was NOT created successfully");
                        throw new Exception($"Subscription was not created successfully for client {_clientId}");
                    }
                }
                catch (ServiceResultException ex)
                {
                    _logger.LogError(ex, $"💥 Client {_clientId} subscription creation FAILED: {ex.StatusCode} - {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Client {_clientId} failed to start subscription");
                throw;
            }
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

                                // İlk 10 veri için debug log
                                if (_totalMessagesReceived <= 10)
                                {
                                    _logger.LogInformation($"🎯 Client {_clientId} DATA #{_totalMessagesReceived}: {tagInfo.TagName} = {doubleValue} [DeviceId={tagInfo.DeviceId}]");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"❌ Client {_clientId} error processing data change item");
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

                            // Her 100 mesajda bir log
                            if (_totalMessagesReceived % 100 == 0)
                            {
                                _logger.LogInformation($"📊 Client {_clientId} processed {valueCount} values, total messages: {_totalMessagesReceived}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"❌ Client {_clientId} database write failed");
                        }
                    });

                    _lastDataReceived = DateTime.Now;

                    // Event fire et
                    var eventArgs = new OpcDataChangedEventArgs
                    {
                        DriverId = _driverInfo.DriverId,
                        DriverName = $"{_driverInfo.DriverName}-Client{_clientId}",
                        TagValues = new List<OpcTagValue>(),
                        Timestamp = DateTime.Now
                    };

                    DataChanged?.Invoke(this, eventArgs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Client {_clientId} data changed handler error");
            }
        }

        public async Task CheckConnectionAsync()
        {
            try
            {
                if (_session?.Connected == true)
                {
                    var nodesToRead = new ReadValueIdCollection();
                    nodesToRead.Add(new ReadValueId { NodeId = Variables.Server_ServerStatus_CurrentTime, AttributeId = Attributes.Value });

                    _session.Read(null, 0, TimestampsToReturn.Both, nodesToRead, out var results, out var diagnosticInfos);

                    // DÜZELTME: results array kontrolü
                    if (results != null && results.Count > 0 && StatusCode.IsGood(results[0].StatusCode))
                    {
                        if (_connectionStatus != OpcConnectionStatus.Connected)
                        {
                            UpdateConnectionStatus(OpcConnectionStatus.Connected, "Connection verified");
                        }
                    }
                    else
                    {
                        UpdateConnectionStatus(OpcConnectionStatus.Error, "Server status read failed");
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
                LastConnected = DateTime.Now,
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