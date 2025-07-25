// Koru1000.KepServerService/Services/KepServerClientPool.cs
using Koru1000.Core.Models.OpcModels;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Koru1000.KepServerService.Clients;

namespace Koru1000.KepServerService.Services
{
    public class KepServerClientPool : IDisposable
    {
        private readonly ILogger<KepServerClientPool> _logger;
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly ISharedQueueService _queueService;
        private readonly ILoggerFactory _loggerFactory;

        // Client tracking
        private readonly ConcurrentDictionary<int, List<OpcClient>> _driverClients;
        private readonly ConcurrentDictionary<int, OpcDriverInfo> _driverInfos;
        private readonly Timer _statusTimer;

        // Statistics
        private long _totalMessagesReceived;
        private long _totalActiveClients;
        private long _totalActiveTags;
        private DateTime _startTime;
        private bool _isDisposed;

        // Events
        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public KepServerClientPool(
            ILogger<KepServerClientPool> logger,
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            ISharedQueueService queueService,
            ILoggerFactory loggerFactory)
        {
            _logger = logger;
            _dbManager = dbManager;
            _queueService = queueService;
            _loggerFactory = loggerFactory;

            _driverClients = new ConcurrentDictionary<int, List<OpcClient>>();
            _driverInfos = new ConcurrentDictionary<int, OpcDriverInfo>();
            _startTime = DateTime.Now;

            // Status timer - her 30 saniyede bir status kontrolü
            _statusTimer = new Timer(StatusCheck, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _logger.LogInformation("🏊‍♂️ KepServerClientPool oluşturuldu");
        }

        public async Task StartAsync(OpcDriverInfo driverInfo)
        {
            try
            {
                _logger.LogInformation($"🏊‍♂️ Driver Client Pool başlatılıyor: {driverInfo.DriverName}");

                // Driver info'yu sakla
                _driverInfos.TryAdd(driverInfo.DriverId, driverInfo);

                // Driver config
                var driverConfig = new EnhancedDriverConfig
                {
                    ConnectionSettings = driverInfo.ConnectionSettings,
                    Security = driverInfo.Security,
                    Credentials = driverInfo.Credentials,
                    EndpointUrl = driverInfo.EndpointUrl,
                    Namespace = driverInfo.Namespace,
                    AddressFormat = driverInfo.AddressFormat,
                    // ✅ Strategy ayarlarını parse et
                    StartupStrategy = ParseStartupStrategy(driverInfo),
                    WaitForData = ParseWaitForData(driverInfo),
                    ClientStartDelay = ParseClientStartDelay(driverInfo)
                };

                var driverSpecificLimits = driverConfig.CreateClientLimits();

                _logger.LogInformation($"🔧 Driver {driverInfo.DriverName} Settings:");
                _logger.LogInformation($"   • Strategy: {driverConfig.StartupStrategy}");
                _logger.LogInformation($"   • Wait for Data: {driverConfig.WaitForData}");
                _logger.LogInformation($"   • Client Delay: {driverConfig.ClientStartDelay}ms");
                _logger.LogInformation($"   • Max Tags: {driverSpecificLimits.MaxTagsPerSubscription}");

                // Tag'leri yükle
                var allTags = await LoadDriverTagsAsync(driverInfo.DriverId);
                if (!allTags.Any())
                {
                    _logger.LogWarning($"⚠️ Driver {driverInfo.DriverName} için tag bulunamadı");
                    return;
                }

                // Tag'leri client'lara böl
                var clientGroups = allTags
                    .Select((tag, index) => new { tag, index })
                    .GroupBy(x => x.index / driverSpecificLimits.MaxTagsPerSubscription)
                    .Select(g => g.Select(x => x.tag).ToList())
                    .ToList();

                _logger.LogInformation($"🔄 {allTags.Count} tag, {clientGroups.Count} client'a bölündü");

                // Mevcut client'ları temizle
                await StopAsync(driverInfo.DriverId);

                // ✅ STRATEGY'YE GÖRE BAŞLATMA
                List<OpcClient> driverClients;

                if (driverConfig.StartupStrategy == ClientStartupStrategy.Sequential)
                {
                    driverClients = await StartClientsSequentialAsync(driverInfo, clientGroups, driverSpecificLimits, driverConfig);
                }
                else
                {
                    driverClients = await StartClientsParallelAsync(driverInfo, clientGroups, driverSpecificLimits, driverConfig);
                }

                if (driverClients.Count == 0)
                {
                    throw new Exception($"❌ Hiçbir client başlatılamadı! Driver: {driverInfo.DriverName}");
                }

                // Driver'ı pool'a ekle
                _driverClients.TryAdd(driverInfo.DriverId, driverClients);
                _totalActiveClients += driverClients.Count;
                _totalActiveTags += allTags.Count;

                _logger.LogInformation($"🎯 Driver Client Pool AKTİF: {driverInfo.DriverName} " +
                    $"- {driverClients.Count}/{clientGroups.Count} client, {allTags.Count} tag");

                await UpdateDriverStatusInDatabase(driverInfo.DriverId, "Connected",
                    $"{driverClients.Count} clients active with {allTags.Count} tags");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Driver Client Pool başlatılamadı: {driverInfo.DriverName}");
                await UpdateDriverStatusInDatabase(driverInfo.DriverId, "Error", ex.Message);
                throw;
            }
        }
        // ✅ Helper Method: Subscription aktif mi kontrol et
        // ✅ SEQUENTIAL (Sıralı) Başlatma Method
        private async Task<List<OpcClient>> StartClientsSequentialAsync(
    OpcDriverInfo driverInfo,
    List<List<OpcTagInfo>> clientGroups,
    ClientLimits limits,
    EnhancedDriverConfig config)
        {
            var driverClients = new List<OpcClient>();

            _logger.LogInformation($"🐢 SEQUENTIAL başlatma - {clientGroups.Count} client sırayla");

            for (int i = 0; i < clientGroups.Count; i++)
            {
                var clientId = i + 1;
                var clientTags = clientGroups[i];

                try
                {
                    _logger.LogInformation($"🔨 Client {clientId}/{clientGroups.Count} başlatılıyor: {clientTags.Count} tag");

                    var opcClient = await CreateAndStartClientAsync(clientId, driverInfo, clientTags, limits);
                    driverClients.Add(opcClient);

                    // ✅ Eski kodunuzdaki gibi - session oluştuktan sonra kısa bekle
                    await Task.Delay(2000); // 2 saniye session için

                    // ✅ Subscription aktif olana kadar bekle
                    await WaitForSubscriptionReady(opcClient, clientId);

                    _logger.LogInformation($"✅ Client {clientId} hazır ve aktif");

                    // ✅ Sonraki client için delay (KEP Server yükünü azalt)
                    if (i < clientGroups.Count - 1)
                    {
                        await Task.Delay(config.ClientStartDelay);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"❌ Client {clientId} başarısız - atlanıyor");
                }
            }

            _logger.LogInformation($"🎯 Sequential completed: {driverClients.Count}/{clientGroups.Count} başarılı");
            return driverClients;
        }
        // ✅ Subscription hazır mı kontrol - eski kod mantığı
        private async Task WaitForSubscriptionReady(OpcClient opcClient, int clientId)
        {
            const int maxWaitSeconds = 30;
            const int checkIntervalMs = 1000;

            for (int i = 0; i < maxWaitSeconds; i++)
            {
                try
                {
                    var status = await opcClient.GetStatusAsync();
                    if (status.ActiveSubscriptions > 0 && status.TotalTagsSubscribed > 0)
                    {
                        _logger.LogInformation($"✅ Client {clientId}: Subscription READY ({status.TotalTagsSubscribed} tags, {i + 1}s)");
                        return;
                    }
                }
                catch { }

                await Task.Delay(checkIntervalMs);
            }

            _logger.LogWarning($"⚠️ Client {clientId}: Subscription {maxWaitSeconds}s içinde hazır olmadı");
        }
        // ✅ PARALLEL (Paralel) Başlatma Method  
        private async Task<List<OpcClient>> StartClientsParallelAsync(
            OpcDriverInfo driverInfo,
            List<List<OpcTagInfo>> clientGroups,
            ClientLimits limits,
            EnhancedDriverConfig config)
        {
            _logger.LogInformation($"🚀 PARALLEL başlatma - Tüm client'lar aynı anda: {clientGroups.Count} client");

            var clientTasks = new List<Task<OpcClient?>>();

            // ✅ Tüm client'ları paralel başlat
            for (int i = 0; i < clientGroups.Count; i++)
            {
                var clientId = i + 1;
                var clientTags = clientGroups[i];

                var task = StartSingleClientAsync(clientId, driverInfo, clientTags, limits, config);
                clientTasks.Add(task);

                // Küçük delay - aynı anda 100 client açılmasın
                if (i % 5 == 0 && i > 0) // Her 5 client'ta bir 500ms delay
                {
                    await Task.Delay(500);
                }
            }

            // Tüm client'ların bitmesini bekle
            var results = await Task.WhenAll(clientTasks);

            // Başarılı olanları filtrele
            var successfulClients = results.Where(client => client != null).ToList();
            var failedCount = clientGroups.Count - successfulClients.Count;

            _logger.LogInformation($"🎯 Parallel tamamlandı: {successfulClients.Count}/{clientGroups.Count} client başarılı, {failedCount} başarısız");

            return successfulClients!;
        }

        // ✅ Tek Client Başlatma (Parallel için)
        private async Task<OpcClient?> StartSingleClientAsync(
            int clientId,
            OpcDriverInfo driverInfo,
            List<OpcTagInfo> clientTags,
            ClientLimits limits,
            EnhancedDriverConfig config)
        {
            try
            {
                _logger.LogInformation($"🚀 Client {clientId} başlatılıyor (parallel): {clientTags.Count} tag");

                var opcClient = await CreateAndStartClientAsync(clientId, driverInfo, clientTags, limits);

                // Veri bekleme opsiyonel
                if (config.WaitForData)
                {
                    await WaitForSubscriptionActive(opcClient, clientId);
                    // ✅ İlk veri beklemeyi kaldır - çok yavaşlatıyor
                    // await WaitForFirstData(opcClient, clientId);
                }

                _logger.LogInformation($"✅ Client {clientId} paralel başarılı");
                return opcClient;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {clientId} paralel başarısız");
                return null;
            }
        }

        // ✅ Client Oluştur ve Başlat
        private async Task<OpcClient> CreateAndStartClientAsync(
            int clientId,
            OpcDriverInfo driverInfo,
            List<OpcTagInfo> clientTags,
            ClientLimits limits)
        {
            var opcClient = new OpcClient(
                clientId,
                driverInfo,
                clientTags,
                _dbManager,
                limits,
                _loggerFactory.CreateLogger<OpcClient>());

            opcClient.DataChanged += (sender, e) => OnClientDataChanged(driverInfo.DriverId, sender, e);
            opcClient.StatusChanged += (sender, e) => OnClientStatusChanged(driverInfo.DriverId, sender, e);

            await opcClient.StartAsync();
            return opcClient;
        }

        // Helper Methods
        private ClientStartupStrategy ParseStartupStrategy(OpcDriverInfo driverInfo)
        {
            try
            {
                if (driverInfo.CustomSettings.TryGetValue("startupStrategy", out var strategy))
                {
                    return strategy.ToString()?.ToLower() switch
                    {
                        "sequential" => ClientStartupStrategy.Sequential,
                        "parallel" => ClientStartupStrategy.Parallel,
                        _ => ClientStartupStrategy.Parallel
                    };
                }
            }
            catch { }

            return ClientStartupStrategy.Parallel; // Default
        }

        private bool ParseWaitForData(OpcDriverInfo driverInfo)
        {
            try
            {
                if (driverInfo.CustomSettings.TryGetValue("waitForData", out var wait))
                {
                    return Convert.ToBoolean(wait);
                }
            }
            catch { }

            return false; // Default
        }

        private int ParseClientStartDelay(OpcDriverInfo driverInfo)
        {
            try
            {
                if (driverInfo.CustomSettings.TryGetValue("clientStartDelay", out var delay))
                {
                    return Convert.ToInt32(delay);
                }
            }
            catch { }

            return 1000; // Default 1 saniye
        }
        private async Task WaitForSubscriptionActive(OpcClient opcClient, int clientId)
        {
            const int maxWaitSeconds = 15;
            const int checkIntervalMs = 1000;

            _logger.LogInformation($"⏳ Client {clientId}: Subscription aktif olana kadar bekleniyor...");

            for (int i = 0; i < maxWaitSeconds; i++)
            {
                var status = await opcClient.GetStatusAsync();
                if (status.ActiveSubscriptions > 0)
                {
                    _logger.LogInformation($"✅ Client {clientId}: Subscription AKTİF ({i + 1}s sonra)");
                    return;
                }

                await Task.Delay(checkIntervalMs);
            }

            _logger.LogWarning($"⚠️ Client {clientId}: Subscription {maxWaitSeconds}s içinde aktif olmadı");
        }

        // ✅ Helper Method: İlk veri gelene kadar bekle
        private async Task WaitForFirstData(OpcClient opcClient, int clientId)
        {
            const int maxWaitSeconds = 30;
            const int checkIntervalMs = 2000;

            _logger.LogInformation($"⏳ Client {clientId}: İlk veri gelene kadar bekleniyor...");

            var initialMessageCount = opcClient.TotalMessagesReceived;

            for (int i = 0; i < maxWaitSeconds; i += 2)
            {
                await Task.Delay(checkIntervalMs);

                var currentMessageCount = opcClient.TotalMessagesReceived;
                if (currentMessageCount > initialMessageCount)
                {
                    _logger.LogInformation($"🎯 Client {clientId}: İLK VERİ ALINDI! ({currentMessageCount} mesaj, {i + 2}s sonra)");
                    return;
                }

                if (i % 10 == 0) // Her 10 saniyede bir log
                {
                    _logger.LogInformation($"⏳ Client {clientId}: Hala veri bekleniyor... ({i + 2}s)");
                }
            }

            _logger.LogWarning($"⚠️ Client {clientId}: {maxWaitSeconds}s içinde veri gelmedi - devam ediliyor");
        }

        // ✅ Helper Method: NodeID'leri test et
        private async Task TestNodeIdsAsync(OpcDriverInfo driverInfo, List<OpcTagInfo> testTags, ClientLimits limits)
        {
            try
            {
                _logger.LogInformation($"🧪 NodeID Test başlatılıyor - {testTags.Count} tag test edilecek");

                var testClient = new OpcClient(
                    999, // Test client ID
                    driverInfo,
                    testTags,
                    _dbManager,
                    limits,
                    _loggerFactory.CreateLogger<OpcClient>());

                await testClient.StartAsync();

                // 10 saniye veri bekle
                await Task.Delay(10000);

                var status = await testClient.GetStatusAsync();

                _logger.LogInformation($"🧪 NodeID Test sonucu: {status.TotalMessagesReceived} mesaj alındı");

                if (status.TotalMessagesReceived == 0)
                {
                    _logger.LogWarning($"⚠️ Test tag'lerden hiç veri gelmedi - NodeID formatı kontrol edilmeli!");

                    // Test tag'lerin NodeID'lerini logla
                    foreach (var tag in testTags)
                    {
                        _logger.LogWarning($"   Test NodeID: {tag.NodeId}");
                    }
                }

                await testClient.StopAsync();
                testClient.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🧪 NodeID test hatası - devam ediliyor");
            }
        }

        public async Task StopAsync(int driverId)
        {
            try
            {
                if (_driverClients.TryRemove(driverId, out var clients))
                {
                    _logger.LogInformation($"🛑 Driver {driverId} client'ları durduruluyor: {clients.Count} client");

                    // Tüm client'ları paralel olarak durdur
                    var stopTasks = clients.Select(client => StopClientAsync(client));
                    await Task.WhenAll(stopTasks);

                    // Client'ları dispose et
                    foreach (var client in clients)
                    {
                        try
                        {
                            client.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"❌ Client dispose hatası");
                        }
                    }

                    // İstatistikleri güncelle
                    _totalActiveClients -= clients.Count;

                    _logger.LogInformation($"✅ Driver {driverId} client'ları durduruldu");
                }

                // Driver info'yu da kaldır
                _driverInfos.TryRemove(driverId, out _);

                // Driver status'unu güncelle
                await UpdateDriverStatusInDatabase(driverId, "Disconnected", "Driver stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Driver {driverId} durdurulamadı");
            }
        }

        public async Task StopAllAsync()
        {
            try
            {
                _logger.LogInformation($"🛑 Tüm driver'lar durduruluyor: {_driverClients.Count} driver");

                var stopTasks = _driverClients.Keys.Select(driverId => StopAsync(driverId));
                await Task.WhenAll(stopTasks);

                _logger.LogInformation("✅ Tüm driver'lar durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Tüm driver'lar durdurulamadı");
            }
        }

        public async Task<ClientPoolStatistics> GetStatisticsAsync()
        {
            var uptime = DateTime.Now - _startTime;
            var activeDrivers = _driverClients.Count;
            var totalClients = _driverClients.Values.Sum(clients => clients.Count);

            // Her driver için detaylı istatistik
            var driverStats = new List<DriverStatistics>();
            foreach (var kvp in _driverClients)
            {
                var driverId = kvp.Key;
                var clients = kvp.Value;
                var driverInfo = _driverInfos.GetValueOrDefault(driverId);

                var driverStat = new DriverStatistics
                {
                    DriverId = driverId,
                    DriverName = driverInfo?.DriverName ?? "Unknown",
                    ClientCount = clients.Count,
                    TotalTags = 0, // Her client'dan alınacak
                    Status = "Connected", // Client'lardan kontrol edilecek
                    LastDataReceived = DateTime.MinValue
                };

                // Her client'dan istatistik topla
                foreach (var client in clients)
                {
                    try
                    {
                        var clientStatus = await client.GetStatusAsync();
                        driverStat.TotalTags += clientStatus.TotalTagsSubscribed;

                        if (clientStatus.LastDataReceived > driverStat.LastDataReceived)
                            driverStat.LastDataReceived = clientStatus.LastDataReceived;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Client status alınamadı - Driver: {driverId}");
                    }
                }

                driverStats.Add(driverStat);
            }

            return new ClientPoolStatistics
            {
                TotalDrivers = activeDrivers,
                TotalClients = totalClients,
                TotalTags = (int)_totalActiveTags,
                TotalMessagesReceived = _totalMessagesReceived,
                Uptime = uptime,
                DriverStatistics = driverStats,
                LastUpdateTime = DateTime.Now
            };
        }

        // Private Helper Methods
        // KepServerClientPool.cs - LoadDriverTagsAsync methodunu tamamen değiştirin
        // KepServerClientPool.cs - LoadDriverTagsAsync - SQL'i düzeltin
        private async Task<List<OpcTagInfo>> LoadDriverTagsAsync(int driverId)
        {
            try
            {
                // ✅ DOĞRU SQL - Sizin verdiğiniz gibi
                const string sql = @"
            SELECT dtt.id AS DeviceTagId, d.channelName AS ChannelName, CONCAT(d.id) AS DeviceName, 
                   JSON_UNQUOTE(JSON_EXTRACT(dtt.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                   d.id as DeviceId
            FROM channeldevice d
            INNER JOIN devicetypetag dtt ON dtt.deviceTypeId = d.deviceTypeId
            WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61) 
            
            UNION ALL
            
            SELECT dit.id AS DeviceTagId, d.channelName AS ChannelName, CONCAT(d.id) AS DeviceName, 
                   JSON_UNQUOTE(JSON_EXTRACT(dit.tagJson, '$.""common.ALLTYPES_NAME""')) AS TagName,
                   d.id as DeviceId
            FROM channeldevice d
            INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = d.id
            WHERE d.driverId = @DriverId AND d.statusCode IN (11,31,41,61)
            ORDER BY ChannelName, DeviceName, TagName"; // ✅ ORDER BY EKLE

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });

                _logger.LogInformation($"🔍 SQL sorgusu {results.Count()} kayıt döndürdü - Driver: {driverId}");

                var tags = new List<OpcTagInfo>();
                foreach (var result in results) // ✅ SINIR KALDIRIN
                {
                    try
                    {
                        var tagName = Convert.ToString(result.TagName);
                        var channelName = Convert.ToString(result.ChannelName);
                        var deviceName = Convert.ToString(result.DeviceName);

                        if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(deviceName))
                        {
                            continue;
                        }

                        // NodeID: ns=2;s=ChannelName.DeviceName.TagName
                        string nodeId = $"ns=2;s={channelName}.{deviceName}.{tagName}";

                        // Debug - İlk 5 NodeID'yi göster
                        if (tags.Count < 5)
                        {
                            _logger.LogInformation($"🏷️ Generated NodeID {tags.Count + 1}: {nodeId} " +
                                $"(Channel: {channelName}, Device: {deviceName}, Tag: {tagName})");
                        }

                        tags.Add(new OpcTagInfo
                        {
                            TagId = Convert.ToInt32(result.DeviceTagId),
                            DeviceId = Convert.ToInt32(result.DeviceId),
                            ChannelName = channelName,
                            TagName = tagName,
                            NodeId = nodeId,
                            DataType = "", // Gerekirse daha sonra ekleyin
                            IsWritable = false, // Gerekirse daha sonra ekleyin
                            TagAddress = ""
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Tag oluşturma hatası");
                    }
                }

                _logger.LogInformation($"✅ Driver {driverId} için {tags.Count} tag başarıyla yüklendi");
                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Driver {driverId} için tag'ler yüklenemedi");
                return new List<OpcTagInfo>();
            }
        }

        private async Task StartClientAsync(OpcClient opcClient, int clientId, int tagCount)
        {
            try
            {
                _logger.LogInformation($"🚀 Client {clientId} başlatılıyor: {tagCount} tag");

                await opcClient.StartAsync();

                _logger.LogInformation($"✅ Client {clientId} başlatıldı: {tagCount} tag");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {clientId} başlatılamadı");
                throw;
            }
        }

        private async Task StopClientAsync(OpcClient opcClient)
        {
            try
            {
                await opcClient.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client {opcClient.ClientId} durdurulamadı");
            }
        }

        private void OnClientDataChanged(int driverId, object? sender, OpcDataChangedEventArgs e)
        {
            try
            {
                // Shared Queue Service'e gönder
                _ = Task.Run(async () =>
                {
                    await _queueService.EnqueueDataAsync(e);
                });

                // Global event'i fire et
                DataChanged?.Invoke(sender, e);

                // Performance logging
                if (e.TagValues.Count > 0)
                {
                    Interlocked.Add(ref _totalMessagesReceived, e.TagValues.Count);

                    if (_totalMessagesReceived % 1000 == 0)
                    {
                        _logger.LogInformation($"📊 Pool Performance: {_totalMessagesReceived} total messages received");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client data changed event işlenirken hata - Driver: {driverId}");
            }
        }

        private void OnClientStatusChanged(int driverId, object? sender, OpcStatusChangedEventArgs e)
        {
            try
            {
                _logger.LogInformation($"🔄 Client Status Changed - Driver: {driverId}, Status: {e.Status}, Message: {e.Message}");

                // Global event'i fire et
                StatusChanged?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Client status changed event işlenirken hata - Driver: {driverId}");
            }
        }

        private async Task UpdateDriverStatusInDatabase(int driverId, string status, string message)
        {
            try
            {
                const string sql = @"
                    UPDATE channeldevice cd
                    INNER JOIN driver_channeltype_relation dcr ON dcr.channelTypeId IN (
                        SELECT dt.ChannelTypeId FROM devicetype dt WHERE dt.id = cd.deviceTypeId
                    )
                    SET cd.statusCode = @StatusCode, cd.updateTime = NOW()
                    WHERE dcr.driverId = @DriverId";

                int statusCode = status switch
                {
                    "Connected" => 41, // Running
                    "Connecting" => 31, // Connected  
                    "Error" => 50, // Error
                    "Disconnected" => 51, // Stopped
                    _ => 50
                };

                await _dbManager.ExecuteExchangerAsync(sql, new
                {
                    StatusCode = statusCode,
                    DriverId = driverId
                });

                _logger.LogDebug($"📝 Driver {driverId} status güncellendi: {status}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Driver {driverId} status güncellenemedi");
            }
        }

        private void StatusCheck(object? state)
        {
            try
            {
                // Her 30 saniyede bir tüm client'ların durumunu kontrol et
                foreach (var kvp in _driverClients)
                {
                    var driverId = kvp.Key;
                    var clients = kvp.Value;

                    foreach (var client in clients)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.CheckConnectionAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"❌ Client {client.ClientId} status kontrolünde hata - Driver: {driverId}");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Status check hatası");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                _logger.LogInformation("🗑️ KepServerClientPool dispose ediliyor...");

                _statusTimer?.Dispose();

                // Tüm driver'ları durdur
                var stopTask = StopAllAsync();
                stopTask.GetAwaiter().GetResult(); // Dispose'da await kullanamıyoruz

                _isDisposed = true;
                _logger.LogInformation("✅ KepServerClientPool dispose edildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KepServerClientPool dispose hatası");
            }
        }
    }

    // Statistics Models
    public class ClientPoolStatistics
    {
        public int TotalDrivers { get; set; }
        public int TotalClients { get; set; }
        public int TotalTags { get; set; }
        public long TotalMessagesReceived { get; set; }
        public TimeSpan Uptime { get; set; }
        public List<DriverStatistics> DriverStatistics { get; set; } = new();
        public DateTime LastUpdateTime { get; set; }
    }

    public class DriverStatistics
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public int ClientCount { get; set; }
        public int TotalTags { get; set; }
        public string Status { get; set; } = "";
        public DateTime LastDataReceived { get; set; }
    }

    // Enhanced Driver Configuration Model (Worker.cs'den taşındı)
    public class EnhancedDriverConfig
    {
        public string EndpointUrl { get; set; } = "opc.tcp://localhost:49320";
        public string Namespace { get; set; } = "2";
        public string ProtocolType { get; set; } = "OPC";
        public string AddressFormat { get; set; } = "ns={namespace};s={channelName}.{deviceName}.{tagName}";
        public string Description { get; set; } = "";
        public KepSecuritySettings Security { get; set; } = new();
        public KepCredentials Credentials { get; set; } = new();
        public KepConnectionSettings ConnectionSettings { get; set; } = new();

        // ✅ Startup Strategy Properties
        public ClientStartupStrategy StartupStrategy { get; set; } = ClientStartupStrategy.Parallel;
        public bool WaitForData { get; set; } = false;
        public int ClientStartDelay { get; set; } = 1000;

        public ClientLimits CreateClientLimits()
        {
            return new ClientLimits
            {
                MaxTagsPerSubscription = ConnectionSettings.MaxTagsPerSubscription,
                MaxChannelsPerSession = 50,
                MaxDevicesPerSession = 50,
                MaxSubscriptionsPerSession = 10,
                PublishingIntervalMs = ConnectionSettings.PublishingInterval,
                MaxNotificationsPerPublish = Math.Min(ConnectionSettings.MaxTagsPerSubscription / 2, 10000),
                SessionTimeoutMs = ConnectionSettings.SessionTimeout,
                ReconnectDelayMs = ConnectionSettings.ReconnectDelay > 0 ? ConnectionSettings.ReconnectDelay : 5000,
                MaxReconnectAttempts = ConnectionSettings.MaxReconnectAttempts > 0 ? ConnectionSettings.MaxReconnectAttempts : 5
            };
        }
    }

    // ✅ Startup Strategy Enum
    public enum ClientStartupStrategy
    {
        Sequential,  // Sıralı - güvenli ama yavaş
        Parallel     // Paralel - hızlı ama riskli
    }
}