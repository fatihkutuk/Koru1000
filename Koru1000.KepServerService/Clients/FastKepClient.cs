// Koru1000.KepServerService/Clients/FastKepClient.cs
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Koru1000.KepServerService.Services;
using System.Text;
using System.Globalization;

namespace Koru1000.KepServerService.Clients
{
    public class FastKepClient
    {
        private readonly ClientConfig _config;
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ILogger _logger;

        private ApplicationConfiguration? _appConfig;
        private Session? _session;
        private Subscription? _subscription;
        private readonly List<KepTagInfo> _tags;

        private bool _canStop = false;
        private long _totalMessages = 0;
        private DateTime _lastDataReceived = DateTime.MinValue;

        public int ClientId => _config.ClientId;
        public bool IsConnected => _session?.Connected == true;

        public FastKepClient(ClientConfig config, DatabaseManager.DatabaseManager dbManager, ILogger logger)
        {
            _config = config;
            _dbManager = dbManager;
            _logger = logger;
            _tags = new List<KepTagInfo>();
        }

        // ESKİ KOD GİBİ - StartClient()
        public async Task StartClientAsync()
        {
            try
            {
                _logger.LogInformation($"🚀 Client {_config.ClientId} başlatılıyor...");

                // ESKİ KOD SIRASINA GÖRE
                await LoadNoErrorNodesAsync(); // _noErrorNodes = _databaseManager.GetNoErrorNodes(_clientId);
                _canStop = true;
                await CreateSessionAsync();    // CreateSession();
                await LoadSubscriptionItemsAsync(); // CreateSubscriptionItems();
                await CreateSubscriptionAsync(); // CreateSubscription();
                await StartSubscriptionAsync(); // StartSubscription();

                _logger.LogInformation($"✅ Client {_config.ClientId} başlatıldı - {_tags.Count} tag");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_config.ClientId} başlatılamadı");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"🛑 Client {_config.ClientId} durduruluyor...");
                _canStop = false;

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

                _logger.LogInformation($"✅ Client {_config.ClientId} durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_config.ClientId} durdurulamadı");
            }
        }

        // YENİ SQL İLE - GetNoErrorNodes
        // LoadNoErrorNodesAsync - ESKİ sp_getClientSubscriptionList GİBİ
        private async Task LoadNoErrorNodesAsync()
        {
            try
            {
                // Önce device'ları ata
                await AssignDevicesToClientAsync();

                // DEBUG - Stored procedure yerine direkt SQL kullan
                const string debugSql = @"
            SELECT dtt.id AS DeviceTagId, d.channelName AS ChannelName, CONCAT(d.id) AS DeviceName, 
                   JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName
            FROM channeldevice d
            INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
            WHERE d.clientId = @ClientId AND d.statusCode IN (11,31,41,61)
            
            UNION ALL
            
            SELECT dit.id AS DeviceTagId, d.channelName AS ChannelName, CONCAT(d.id) AS DeviceName, 
                   JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName
            FROM channeldevice d
            INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
            WHERE d.clientId = @ClientId AND d.statusCode IN (11,31,41,61)
            ORDER BY ChannelName, DeviceName, TagName";

                _logger.LogInformation($"🔍 Client {_config.ClientId} için tag sorgusu çalıştırılıyor...");

                var results = await _dbManager.QueryExchangerAsync<dynamic>(debugSql, new { ClientId = _config.ClientId });

                _logger.LogInformation($"🔍 Client {_config.ClientId} sorgu sonucu: {results.Count()} satır");

                foreach (var result in results)
                {
                    if (!string.IsNullOrEmpty(result.TagName))
                    {
                        var tag = new KepTagInfo
                        {
                            DeviceTagId = (int)result.DeviceTagId,
                            ChannelName = result.ChannelName ?? "",
                            DeviceName = result.DeviceName ?? "",
                            TagName = result.TagName ?? "",
                            NodeId = $"ns=2;s={result.ChannelName}.{result.DeviceName}.{result.TagName}"
                        };

                        _tags.Add(tag);

                        // İlk 5 tag'ı log'la
                        if (_tags.Count <= 5)
                        {
                            _logger.LogInformation($"🏷️ Tag {_tags.Count}: {tag.ChannelName}.{tag.DeviceName}.{tag.TagName}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ Boş TagName: DeviceTagId={result.DeviceTagId}, ChannelName={result.ChannelName}");
                    }
                }

                _logger.LogInformation($"Client {_config.ClientId}: {_tags.Count} tag yüklendi (DIREKT SQL ile)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_config.ClientId} tag yükleme hatası");
                throw;
            }
        }

        // YENİ SQL İLE - Device atama

        // FastKepClient.cs - AssignDevicesToClientAsync - ESKİ SİSTEM GİBİ
        private async Task AssignDevicesToClientAsync()
        {
            try
            {
                // TAG BAZLI DEVICE ATAMA - ESKİ SİSTEM GİBİ
                const string tagSql = @"
            SELECT dtt.id AS DeviceTagId, d.id as DeviceId
            FROM channeldevice d
            INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
            WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
            
            UNION ALL
            
            SELECT dit.id AS DeviceTagId, d.id as DeviceId
            FROM channeldevice d
            INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
            WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
            ORDER BY DeviceId, DeviceTagId
            LIMIT @TagCount OFFSET @StartIndex";

                var tagResults = await _dbManager.QueryExchangerAsync<dynamic>(tagSql, new
                {
                    DriverId = _config.DriverId,
                    TagCount = _config.TagCount,
                    StartIndex = _config.TagStartIndex
                });

                // Unique device ID'lerini al
                var deviceIds = tagResults.Select(r => (int)r.DeviceId).Distinct().ToList();

                if (deviceIds.Any())
                {
                    var deviceIdList = string.Join(",", deviceIds);
                    const string updateSql = @"
                UPDATE channeldevice 
                SET clientId = @ClientId 
                WHERE FIND_IN_SET(id, @DeviceIds)";

                    await _dbManager.ExecuteExchangerAsync(updateSql, new
                    {
                        ClientId = _config.ClientId,
                        DeviceIds = deviceIdList
                    });

                    _logger.LogInformation($"Client {_config.ClientId}: {deviceIds.Count} device atandı (TagsPerClient ayarı: {_config.TagCount})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_config.ClientId} device atama hatası: {ex.Message}");
            }
        }
        private async Task CreateSessionAsync()
        {
            try
            {
                _appConfig = new ApplicationConfiguration()
                {
                    ApplicationName = $"Koru1000 KEP Client {_config.ClientId}",
                    ApplicationUri = $"urn:localhost:Koru1000:KepClient:{_config.ClientId}",
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = @"Directory",
                            StorePath = @"%LocalApplicationData%\OPC Foundation\pki\own",
                            SubjectName = $"CN=Koru1000 KEP Client {_config.ClientId}, C=US, S=Arizona, O=OPC Foundation, DC=" + Utils.GetHostName()
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
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 300000 }
                };

                await _appConfig.Validate(ApplicationType.Client);

                var applicationInstance = new ApplicationInstance(_appConfig);
                await applicationInstance.CheckApplicationInstanceCertificates(false, 240);

                _appConfig.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = true; };

                var endpointDescription = CoreClientUtils.SelectEndpoint(_appConfig, _config.EndpointUrl, useSecurity: true);
                var endpointConfiguration = EndpointConfiguration.Create(_appConfig);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                IUserIdentity userIdentity;
                if (!string.IsNullOrEmpty(_config.Username))
                {
                    userIdentity = new UserIdentity(_config.Username, _config.Password);
                }
                else
                {
                    userIdentity = new UserIdentity();
                }

                _session = await Session.Create(_appConfig, endpoint, false,
                    $"Koru1000 KEP Client {_config.ClientId}",
                    300000,
                    userIdentity,
                    null);

                _logger.LogInformation($"✅ Client {_config.ClientId} session oluşturuldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_config.ClientId} session oluşturulamadı");
                throw;
            }
        }

        // ESKİ KOD GİBİ - CreateSubscriptionItems
        private async Task LoadSubscriptionItemsAsync()
        {
            // Tag'lar zaten LoadNoErrorNodesAsync'de yüklendi
            await Task.CompletedTask;
        }

        // ESKİ KOD GİBİ - CreateSubscription
        // FastKepClient.cs - CreateSubscriptionAsync metodunu düzeltelim:

        // FastKepClient.cs - CreateSubscriptionAsync metodunu güncelle
        private async Task CreateSubscriptionAsync()
        {
            try
            {
                _subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingInterval = 1000,
                    MaxNotificationsPerPublish = 5000, // AZALT
                    PublishingEnabled = true,
                    KeepAliveCount = 10,
                    LifetimeCount = 100
                };

                // BATCH SIZE'I KÜÇÜLT - 500'den 100'e
                const int batchSize = 5000; // 500 yerine 100
                var totalAdded = 0;

                for (int i = 0; i < _tags.Count; i += batchSize)
                {
                    var batch = _tags.Skip(i).Take(batchSize).ToList();
                    var monitoredItems = new List<MonitoredItem>();

                    foreach (var tag in batch)
                    {
                        var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
                        {
                            DisplayName = $"{tag.ChannelName}.{tag.DeviceName}.{tag.TagName}",
                            StartNodeId = tag.NodeId,
                            AttributeId = Attributes.Value,
                            MonitoringMode = MonitoringMode.Reporting,
                            SamplingInterval = 1000,
                            QueueSize = 1,
                            DiscardOldest = true,
                            Handle = tag
                        };

                        monitoredItems.Add(monitoredItem);
                    }

                    _subscription.AddItems(monitoredItems);
                    totalAdded += monitoredItems.Count;

                    _logger.LogInformation($"Client {_config.ClientId}: {monitoredItems.Count} monitored item eklendi (Toplam: {totalAdded}/{_tags.Count})");

                    // DAHA UZUN BEKLE - server'ın nefes alması için
                    //await Task.Delay(100); // 100ms'den 500ms'e çıkar
                }

                _logger.LogInformation($"✅ Client {_config.ClientId} subscription oluşturuldu - {totalAdded} monitored item");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_config.ClientId} subscription oluşturulamadı");
                throw;
            }
        }        // ESKİ KOD GİBİ - StartSubscription
        private async Task StartSubscriptionAsync()
        {
            try
            {
                // ESKİ KOD GİBİ - FastDataChangeCallback kullan
                _subscription.FastDataChangeCallback = new FastDataChangeNotificationEventHandler(OnDataChanged);

                _session.AddSubscription(_subscription);
                _subscription.Create();

                _logger.LogInformation($"✅ Client {_config.ClientId} subscription başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {_config.ClientId} subscription başlatılamadı");
                throw;
            }
        }

        // ESKİ KOD GİBİ - FastDataChangeCallback handler
        private void OnDataChanged(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
        {
            try
            {
                if (!_canStop) return;

                var textForWrite = new StringBuilder();
                textForWrite.Append("CALL dbdataexchanger.sp_setTagValueOnDataChanged(\"");
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
                                // DeviceId direkt DeviceName'den alınıyor (örnek: '1104')
                                if (int.TryParse(tagInfo.DeviceName, out int deviceId))
                                {
                                    var doubleValue = Convert.ToDouble(item.Value.Value);
                                    textForWrite.Append($"({deviceId},'{tagInfo.TagName}',{doubleValue.ToString("f6", CultureInfo.InvariantCulture)}),");
                                    valueCount++;
                                    _totalMessages++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Client {_config.ClientId} data processing error");
                        }
                    }
                }

                if (valueCount > 0)
                {
                    // Son virgülü kaldır ve stored procedure'u çağır
                    textForWrite.Remove(textForWrite.Length - 1, 1);
                    textForWrite.Append("\")");

                    // Database'e yaz - ESKİ KOD GİBİ
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _dbManager.ExecuteKbinAsync(textForWrite.ToString());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Client {_config.ClientId} database write failed");
                        }
                    });

                    _lastDataReceived = DateTime.Now;

                    // Performance log - Her 1000 mesajda bir
                    if (_totalMessages % 1000 == 0)
                    {
                        _logger.LogInformation($"📊 Client {_config.ClientId} performance: {_totalMessages} total messages");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_config.ClientId} data changed handler error");
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
                        // Connection OK
                    }
                }
                else
                {
                    _logger.LogWarning($"Client {_config.ClientId} connection lost");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Client {_config.ClientId} connection check failed");
            }
        }
    }

    // Yardımcı sınıf
    public class KepTagInfo
    {
        public int DeviceTagId { get; set; }
        public string ChannelName { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string TagName { get; set; } = "";
        public string NodeId { get; set; } = "";
    }
}