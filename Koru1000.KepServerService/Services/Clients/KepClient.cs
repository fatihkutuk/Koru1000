using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
    private readonly KepServiceConfig _config;
    private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
    private readonly ILogger<KepClient> _logger;

    private Session? _session;
    private Subscription? _subscription;
    private ApplicationConfiguration? _appConfig;
    private readonly ConcurrentDictionary<uint, KepTagInfo> _monitoredItems;
    private readonly Timer _writeTimer;
    private readonly Timer _statusTimer;

    private bool _canProcess = false;
    private long _totalMessagesReceived = 0;
    private long _totalMessagesProcessed = 0;
    private DateTime _lastDataReceived = DateTime.MinValue;
    private KepConnectionStatus _connectionStatus = KepConnectionStatus.Disconnected;
    private string _lastError = "";

    public event EventHandler<KepDataChangedEventArgs>? DataChanged;
    public event EventHandler<KepStatusChangedEventArgs>? StatusChanged;

    public int ClientId => _clientId;
    public KepConnectionStatus ConnectionStatus => _connectionStatus;

    public KepClient(
        int clientId,
        KepServiceConfig config,
        Koru1000.DatabaseManager.DatabaseManager dbManager,
        ILogger<KepClient> logger)
    {
        _clientId = clientId;
        _config = config;
        _dbManager = dbManager;
        _logger = logger;
        _monitoredItems = new ConcurrentDictionary<uint, KepTagInfo>();

        _writeTimer = new Timer(WriteToOpcCallback, null, Timeout.Infinite, Timeout.Infinite);
        _statusTimer = new Timer(StatusUpdateCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        try
        {
            _logger.LogInformation($"KEP Client {_clientId} başlatılıyor...");

            await CreateApplicationConfigurationAsync();
            await CreateSessionAsync();
            await CreateSubscriptionAsync();
            await CreateMonitoredItemsAsync();
            await StartSubscriptionAsync();

            _canProcess = true;
            UpdateConnectionStatus(KepConnectionStatus.Connected, "Client started successfully");

            // Timer'ları başlat
            _writeTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _statusTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            _logger.LogInformation($"KEP Client {_clientId} başarıyla başlatıldı");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"KEP Client {_clientId} başlatılamadı");
            UpdateConnectionStatus(KepConnectionStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _logger.LogInformation($"KEP Client {_clientId} durduruluyor...");
            _canProcess = false;

            // Timer'ları durdur
            _writeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

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

            UpdateConnectionStatus(KepConnectionStatus.Disconnected, "Client stopped");
            _logger.LogInformation($"KEP Client {_clientId} durduruldu");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"KEP Client {_clientId} durdurma hatası");
        }
    }

    private async Task CreateApplicationConfigurationAsync()
    {
        var certificateSubjectName = $"CN=Koru1000 KEP Client {_clientId}, C=US, S=Arizona, O=OPC Foundation, DC={Utils.GetHostName()}";

        _appConfig = new ApplicationConfiguration()
        {
            ApplicationName = $"Koru1000 KEP Client {_clientId}",
            ApplicationUri = $"urn:localhost:Koru1000:KepClient:{_clientId}",
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
                AutoAcceptUntrustedCertificates = _config.Security.AutoAcceptUntrustedCertificates,
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
                DefaultSessionTimeout = _config.Limits.SessionTimeoutMs
            }
        };

        await _appConfig.Validate(ApplicationType.Client);

        var applicationInstance = new ApplicationInstance(_appConfig);
        bool certificateValid = await applicationInstance.CheckApplicationInstanceCertificates(false, 240);

        if (!certificateValid)
        {
            throw new Exception($"Application certificate invalid for client {_clientId}!");
        }

        _appConfig.CertificateValidator.CertificateValidation += (s, e) => {
            _logger.LogDebug("Client {ClientId} Certificate validation: {Subject} - ACCEPTING", _clientId, e.Certificate?.Subject);
            e.Accept = true;
        };
    }

    private async Task CreateSessionAsync()
    {
        var endpointUrl = _config.Connection.EndpointUrl;
        var endpointDescription = CoreClientUtils.SelectEndpoint(
            _appConfig,
            endpointUrl,
            useSecurity: _config.Security.UseSecureConnection);

        var endpointConfiguration = EndpointConfiguration.Create(_appConfig);
        endpointConfiguration.OperationTimeout = _config.Connection.ConnectTimeoutMs;
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        IUserIdentity userIdentity = _config.Security.UserTokenType == "UserName" &&
                                   !string.IsNullOrEmpty(_config.Security.Username)
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
        _subscription = new Subscription(_session!.DefaultSubscription)
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
    private async Task<DriverCustomSettings?> GetDriverSettingsAsync()
    {
        try
        {
            const string sql = @"
            SELECT d.customSettings
            FROM driver d
            INNER JOIN drivertype dt ON d.driverTypeId = dt.id
            WHERE dt.name = 'KEPSERVEREX'
            ORDER BY d.id
            LIMIT 1";

            var result = await _dbManager.QueryExchangerAsync<dynamic>(sql);
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
    public async Task UnsubscribeDeviceTagsAsync(int deviceId)
    {
        try
        {
            if (_subscription == null || _session?.Connected != true)
            {
                _logger.LogWarning($"⚠️ Client {_clientId} - Subscription veya session aktif değil");
                return;
            }

            var itemsToRemove = new List<MonitoredItem>();

            foreach (var item in _subscription.MonitoredItems)
            {
                if (item.Handle is KepTagInfo tagInfo && tagInfo.DeviceId == deviceId)
                {
                    itemsToRemove.Add(item);
                }
            }

            if (itemsToRemove.Any())
            {
                _logger.LogInformation($"🔗 Client {_clientId} - Device {deviceId} için {itemsToRemove.Count} tag unsubscribe ediliyor");

                _subscription.RemoveItems(itemsToRemove);

                foreach (var item in itemsToRemove)
                {
                    _monitoredItems.TryRemove(item.ClientHandle, out _);
                }

                _logger.LogInformation($"✅ Client {_clientId} - Device {deviceId} unsubscribe tamamlandı");
            }
            else
            {
                _logger.LogInformation($"📋 Client {_clientId} - Device {deviceId} için unsubscribe edilecek tag bulunamadı");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Client {_clientId} - Device {deviceId} unsubscribe hatası");
        }
    }
    private void WriteTimerCallback(object? state)
    {
        try
        {
            if (!_canProcess || _session?.Connected != true) return;

            _ = Task.Run(async () =>
            {
                await ProcessWriteRequestsAsync();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} write timer hatası");
        }
    }

    // Yeni method ekle
    private async Task ProcessWriteRequestsAsync()
    {
        try
        {
            // Bu client'a ait yazılacak tag'leri al
            var writeRequests = await GetWriteRequestsForClientAsync();

            if (!writeRequests.Any()) return;

            var writeValues = new WriteValueCollection();

            foreach (var request in writeRequests)
            {
                try
                {
                    // DeviceId'dan channel ve device ismini bul
                    var deviceInfo = await GetDeviceInfoAsync(request.DeviceId);
                    if (deviceInfo == null) continue;

                    var nodeId = $"ns=2;s={deviceInfo.ChannelName}.{request.DeviceId}.{request.TagName}";

                    var writeValue = new WriteValue
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value,
                        Value = new DataValue(new Variant(request.TagValue))
                    };

                    writeValues.Add(writeValue);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Write value hazırlama hatası: DeviceId={request.DeviceId}, Tag={request.TagName}");
                }
            }

            if (writeValues.Any())
            {
                // OPC'ye yaz
                _session!.Write(null, writeValues, out var results, out var diagnosticInfos);

                // Başarılı yazılanları _tagyaz tablosundan sil
                await DeleteWrittenTagsAsync(writeRequests.Where((req, index) =>
                    index < results.Count && StatusCode.IsGood(results[index])).ToList());

                _logger.LogDebug($"Client {_clientId}: {results.Count(StatusCode.IsGood)} tag yazıldı, {results.Count(r => !StatusCode.IsGood(r))} hata");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} write processing hatası");
        }
    }

    // Bu client'a ait yazma isteklerini al
    private async Task<List<WriteRequest>> GetWriteRequestsForClientAsync()
    {
        try
        {
            const string sql = @"
            SELECT t.devId, t.tagName, t.tagValue
            FROM kbindb._tagyaz t
            INNER JOIN channeldevice cd ON t.devId = cd.id
            WHERE cd.clientId = @ClientId
            ORDER BY t.time
            LIMIT 100"; // Aynı anda max 100 tag yaz

            var results = await _dbManager.QueryKbinAsync<dynamic>(sql, new { ClientId = _clientId });

            return results.Select(r => new WriteRequest
            {
                DeviceId = (int)r.devId,
                TagName = r.tagName?.ToString() ?? "",
                TagValue = Convert.ToSingle(r.tagValue)
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} write requests alınamadı");
            return new List<WriteRequest>();
        }
    }

    // DeviceId'dan channel ve device bilgisini al
    private async Task<DeviceInfo?> GetDeviceInfoAsync(int deviceId)
    {
        try
        {
            const string sql = @"
            SELECT id as DeviceId, channelName as ChannelName
            FROM channeldevice 
            WHERE id = @DeviceId";

            var result = await _dbManager.QueryFirstExchangerAsync<dynamic>(sql, new { DeviceId = deviceId });

            if (result != null)
            {
                return new DeviceInfo
                {
                    DeviceId = (int)result.DeviceId,
                    ChannelName = result.ChannelName?.ToString() ?? ""
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Device info alınamadı: {deviceId}");
            return null;
        }
    }

    // Başarıyla yazılan tag'leri sil
    private async Task DeleteWrittenTagsAsync(List<WriteRequest> writtenRequests)
    {
        try
        {
            foreach (var request in writtenRequests)
            {
                const string deleteSql = @"
                DELETE FROM kbindb._tagyaz 
                WHERE devId = @DeviceId AND tagName = @TagName";

                await _dbManager.ExecuteKbinAsync(deleteSql, new
                {
                    DeviceId = request.DeviceId,
                    TagName = request.TagName
                });
            }

            _logger.LogDebug($"Client {_clientId}: {writtenRequests.Count} yazılmış tag silindi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} yazılmış tag'ları silme hatası");
        }
    }

    // Helper class'lar
    public class WriteRequest
    {
        public int DeviceId { get; set; }
        public string TagName { get; set; } = "";
        public float TagValue { get; set; }
    }

    public class DeviceInfo
    {
        public int DeviceId { get; set; }
        public string ChannelName { get; set; } = "";
    }
    private async Task<int> GetMaxTagsPerClientFromDriverAsync()
    {
        try
        {
            const string sql = @"
            SELECT customSettings 
            FROM driver 
            WHERE id = (SELECT DISTINCT driverId FROM channeldevice WHERE driverId IS NOT NULL LIMIT 1)";

            var connectionSettings = await _dbManager.QueryFirstExchangerAsync<string>(sql);

            if (!string.IsNullOrEmpty(connectionSettings))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(connectionSettings);
                if (settings != null && settings.ContainsKey("maxTagsPerClient"))
                {
                    return Convert.ToInt32(settings["maxTagsPerClient"]);
                }
            }

            return 30000;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driver JSON'dan maxTagsPerClient okunamadı");
            return 30000;
        }
    }
    private async Task CreateMonitoredItemsAsync()
    {
        try
        {
            var allTags = await GetClientTagsAsync();
            _logger.LogInformation($"Client {_clientId} için toplam {allTags.Count} tag bulundu");

            if (!allTags.Any())
            {
                _logger.LogWarning($"Client {_clientId} için tag bulunamadı");
                return;
            }

            var maxTags = await GetMaxTagsPerClientFromDriverAsync();
            var tagsToUse = allTags.Take(maxTags).ToList();

            if (allTags.Count > maxTags)
            {
                _logger.LogWarning($"⚠️ Client {_clientId} için {allTags.Count} tag var, sadece {maxTags} tanesi kullanılacak!");
            }

            var monitoredItemList = new List<MonitoredItem>();

            foreach (var tag in tagsToUse)
            {
                try
                {
                    // System tag'lar için özel NodeId formatı
                    string nodeId;
                    if (tag.TagName.StartsWith("_"))
                    {
                        nodeId = $"ns=2;s={tag.ChannelName}.{tag.DeviceName}._System.{tag.TagName}";
                    }
                    else
                    {
                        nodeId = $"ns=2;{tag.ChannelName}.{tag.DeviceName}.{tag.TagName}";
                    }

                    var monitoredItem = new MonitoredItem(_subscription!.DefaultItem)
                    {
                        DisplayName = tag.TagName.StartsWith("_")
                            ? $"{tag.ChannelName}.{tag.DeviceName}._System.{tag.TagName}"
                            : $"{tag.ChannelName}.{tag.DeviceName}.{tag.TagName}",
                        StartNodeId = nodeId,
                        AttributeId = Attributes.Value,
                        MonitoringMode = MonitoringMode.Reporting,
                        SamplingInterval = 2000,
                        QueueSize = 1,
                        DiscardOldest = true,
                        Handle = tag
                    };

                    monitoredItemList.Add(monitoredItem);
                    _monitoredItems.TryAdd(monitoredItem.ClientHandle, tag);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Client {ClientId} monitored item oluşturulamadı: {TagName}", _clientId, tag.TagName);
                }
            }

            _subscription.AddItems(monitoredItemList);
            _logger.LogInformation("Client {ClientId} {Count} monitored item oluşturuldu", _clientId, monitoredItemList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} monitored items oluşturulamadı");
            throw;
        }
    }
    private async Task<int> GetMaxTagsPerClientFromConfigAsync()
    {
        try
        {
            const string sql = @"
            SELECT TOP 1 connectionSettings 
            FROM driver 
            ORDER BY id";

            var result = await _dbManager.QueryFirstExchangerAsync<string>(sql);

            if (!string.IsNullOrEmpty(result))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(result);
                if (settings != null && settings.ContainsKey("maxTagsPerClient"))
                {
                    return Convert.ToInt32(settings["maxTagsPerClient"]);
                }
            }

            return 30000;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MaxTagsPerClient okunamadı, default 30000 kullanılıyor");
            return 30000;
        }
    }
    private async Task<List<KepTagInfo>> GetClientTagsAsync()
    {
        try
        {
            _logger.LogInformation($"🏷️ Client {_clientId} için tag'ler sorgulanıyor...");

            // Normal tag'ları al
            const string sql = @"CALL sp_getClientSubscriptionList_NEW(@p_clientId)";
            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { p_clientId = _clientId });

            var tags = results.Where(r => !string.IsNullOrEmpty(r.TagName?.ToString()))
                             .Select(r => new KepTagInfo
                             {
                                 DeviceTagId = (int)r.DeviceTagId,
                                 DeviceId = int.Parse(r.DeviceName),
                                 ChannelName = r.ChannelName ?? "",
                                 DeviceName = r.DeviceName ?? "",
                                 TagName = r.TagName ?? ""
                             }).ToList();

            // SYSTEM TAG'LARINI EKLE
            var systemTags = await GetSystemTagsForClientAsync();
            tags.AddRange(systemTags);

            _logger.LogInformation($"📊 Client {_clientId} Tag İstatistikleri:");
            _logger.LogInformation($"   • Normal tag: {tags.Count - systemTags.Count:N0}");
            _logger.LogInformation($"   • System tag: {systemTags.Count:N0}");
            _logger.LogInformation($"   • Toplam tag: {tags.Count:N0}");

            return tags;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Client {_clientId} tag'leri alınamadı");
            return new List<KepTagInfo>();
        }
    }
    private async Task StartSubscriptionAsync()
    {
        try
        {
            _subscription!.FastDataChangeCallback = OnDataChanged;
            _session!.AddSubscription(_subscription);

            // Subscription'ı küçük batch'ler halinde oluştur
            _subscription.Create();

            _logger.LogInformation("Client {ClientId} subscription başlatıldı", _clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client {ClientId} subscription başlatılamadı", _clientId);
            throw;
        }
    }

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
                        if (monitoredItem?.Handle is KepTagInfo tagInfo)
                        {
                            var doubleValue = Convert.ToDouble(item.Value.Value);
                            textForWrite.Append($"({tagInfo.DeviceId},'{tagInfo.TagName}',{doubleValue.ToString("f6", CultureInfo.InvariantCulture)}),");
                            valueCount++;
                            _totalMessagesReceived++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Client {ClientId} data change item işleme hatası", _clientId);
                    }
                }
            }

            if (valueCount > 0)
            {
                // Son virgülü kaldır
                textForWrite.Remove(textForWrite.Length - 1, 1);
                textForWrite.Append("\")");

                // kbindb'ye yaz
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dbManager.ExecuteExchangerAsync(textForWrite.ToString());
                        _totalMessagesProcessed += valueCount;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Client {ClientId} database write hatası", _clientId);
                    }
                });

                _lastDataReceived = DateTime.Now;

                // Debug log
                if (_totalMessagesReceived <= 100)
                {
                    _logger.LogInformation("Client {ClientId} processed {Count} values, total: {Total}", _clientId, valueCount, _totalMessagesReceived);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client {ClientId} data changed handler hatası", _clientId);
        }
    }

    private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
    {
        try
        {
            if (ServiceResult.IsGood(e.Status))
            {
                if (_connectionStatus != KepConnectionStatus.Connected)
                {
                    UpdateConnectionStatus(KepConnectionStatus.Connected, "Keep alive OK");
                }
            }
            else
            {
                _logger.LogWarning($"Keep alive failed for client {_clientId}: {e.Status}");
                UpdateConnectionStatus(KepConnectionStatus.Error, $"Keep alive failed: {e.Status}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Keep alive error for client {_clientId}");
        }
    }

    private void WriteToOpcCallback(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteToOpcAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_clientId} write to OPC hatası");
            }
        });
    }

    private void StatusUpdateCallback(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateClientStatusAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_clientId} status update hatası");
            }
        });
    }

    private async Task WriteToOpcAsync()
    {
        try
        {
            if (_session?.Connected != true) return;

            var writeTags = await GetWriteTagsAsync();
            if (!writeTags.Any()) return;

            var nodesToWrite = new WriteValueCollection();

            foreach (var writeTag in writeTags)
            {
                try
                {
                    var nodeId = new NodeId(writeTag.NodeId);
                    var systemType = GetSystemType(nodeId);

                    nodesToWrite.Add(new WriteValue
                    {
                        AttributeId = Attributes.Value,
                        Value = new DataValue { Value = Convert.ChangeType(writeTag.Value, systemType) },
                        NodeId = nodeId
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Write tag hazırlama hatası: {writeTag.NodeId}");
                }
            }

            if (nodesToWrite.Count > 0)
            {
                _session.Write(null, nodesToWrite, out var results, out var diagnosticInfos);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} write to OPC hatası");
        }
    }

    private Type GetSystemType(NodeId nodeId)
    {
        try
        {
            if (_session?.NodeCache == null) return typeof(double);

            var node = _session.NodeCache.Find(nodeId) as VariableNode;
            if (node?.DataType == null) return typeof(double);

            var builtInType = TypeInfo.GetBuiltInType(node.DataType);
            return TypeInfo.GetSystemType(builtInType, node.ValueRank);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSystemType hatası: {NodeId}", nodeId);
            return typeof(double); // Default
        }
    }

    private async Task<List<KepWriteTag>> GetWriteTagsAsync()
    {
        try
        {
            // kbindb stored procedure kullan
            const string sql = "CALL sp_getTagsToWrite(@ClientId)";

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { ClientId = _clientId });

            return results.Select(r => new KepWriteTag
            {
                ChannelName = r.ChannelName ?? "",
                DeviceName = r.DeviceName ?? "",
                TagName = r.TagName ?? "",
                Value = r.Value ?? 0
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} write tag'leri alınamadı");
            return new List<KepWriteTag>();
        }
    }

    private async Task UpdateClientStatusAsync()
    {
        try
        {
            string status = (_session?.Connected == true && !_session.KeepAliveStopped) ? "Ok" : "Bad";

            const string sql = "REPLACE INTO service (ClientId, Status) VALUES (@ClientId, @Status)";
            await _dbManager.ExecuteExchangerAsync(sql, new
            {
                ClientId = _clientId,
                Status = status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Client {_clientId} status update hatası");
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
                nodesToRead.Add(new ReadValueId
                {
                    NodeId = Variables.Server_ServerStatus_CurrentTime,
                    AttributeId = Attributes.Value
                });

                _session.Read(null, 0, TimestampsToReturn.Both, nodesToRead,
                    out var results, out var diagnosticInfos);

                if (results != null && results.Count > 0 && StatusCode.IsGood(results[0].StatusCode))
                {
                    if (_connectionStatus != KepConnectionStatus.Connected)
                    {
                        UpdateConnectionStatus(KepConnectionStatus.Connected, "Connection verified");
                    }
                }
            }
            else
            {
                UpdateConnectionStatus(KepConnectionStatus.Disconnected, "Session disconnected");
            }
        }
        catch (Exception ex)
        {
            UpdateConnectionStatus(KepConnectionStatus.Error, ex.Message);
        }
    }

    public async Task<KepClientStatus> GetStatusAsync()
    {
        return await Task.FromResult(new KepClientStatus
        {
            ClientId = _clientId,
            Status = _connectionStatus == KepConnectionStatus.Connected ? "Ok" : "Bad",
            LastUpdate = DateTime.Now,
            TotalMessagesReceived = _totalMessagesReceived,
            TotalMessagesProcessed = _totalMessagesProcessed,
            ActiveSubscriptions = _subscription != null ? 1 : 0,
            TotalTags = _monitoredItems.Count,
            LastError = _lastError
        });
    }
    private async Task<List<KepTagInfo>> GetSystemTagsForClientAsync()
    {
        try
        {
            // Bu client'a ait device'ları al
            const string deviceSql = @"
            SELECT DISTINCT id as DeviceId, channelName as ChannelName
            FROM channeldevice 
            WHERE clientId = @ClientId AND statusCode IN (11,31,41,61)";

            var devices = await _dbManager.QueryExchangerAsync<dynamic>(deviceSql, new { ClientId = _clientId });

            var systemTags = new List<KepTagInfo>();

            foreach (var device in devices)
            {
                var deviceId = (int)device.DeviceId;
                var channelName = device.ChannelName?.ToString() ?? "";

                // Her device için system tag'ları ekle
                var systemTagNames = new[] { "_NoError", "_ReadError", "_WriteError", "_ConnectionStatus" };

                foreach (var tagName in systemTagNames)
                {
                    systemTags.Add(new KepTagInfo
                    {
                        DeviceTagId = -1, // System tag'lar için negatif ID
                        DeviceId = deviceId,
                        ChannelName = channelName,
                        DeviceName = deviceId.ToString(),
                        TagName = tagName
                    });
                }
            }

            return systemTags;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"System tag'ları alınamadı: Client {_clientId}");
            return new List<KepTagInfo>();
        }
    }
    private void UpdateConnectionStatus(KepConnectionStatus status, string message)
    {
        if (_connectionStatus != status)
        {
            _connectionStatus = status;
            _lastError = status == KepConnectionStatus.Error ? message : "";

            var eventArgs = new KepStatusChangedEventArgs
            {
                ClientId = _clientId,
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

        _writeTimer?.Dispose();
        _statusTimer?.Dispose();

        try
        {
            _subscription?.Dispose();
            _session?.Dispose();
        }
        catch { }
    }
}