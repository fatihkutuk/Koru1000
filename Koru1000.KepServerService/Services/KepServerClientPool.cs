// Koru1000.KepServerService/Services/KepServerClientPool.cs
using System.Collections.Concurrent;
using Koru1000.Core.Models.OpcModels;
using Koru1000.KepServerService.Clients;
using Microsoft.Extensions.Logging;

namespace Koru1000.KepServerService.Services
{
    public class KepServerClientPool : IDisposable
    {
        private readonly ConcurrentDictionary<int, OpcDriverClient> _clients;
        private readonly ConcurrentDictionary<string, int> _tagToClientMapping;
        private readonly ILogger<KepServerClientPool> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ClientLimits _limits;

        // KEPServerEX Pool Ayarları - Driver JSON'dan gelecek
        private readonly int _maxTagsPerClient = 20000;
        private readonly int _publishingIntervalMs = 1000;

        public KepServerClientPool(
            ILogger<KepServerClientPool> logger,
            ILoggerFactory loggerFactory,
            DatabaseManager.DatabaseManager dbManager,
            ClientLimits limits)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _dbManager = dbManager;
            _limits = limits;

            _clients = new ConcurrentDictionary<int, OpcDriverClient>();
            _tagToClientMapping = new ConcurrentDictionary<string, int>();
        }

        public async Task StartAsync(OpcDriverInfo driverInfo)
        {
            try
            {
                _logger.LogInformation("🏊‍♂️ KEPServerEX Client Pool başlatılıyor: {DriverName}", driverInfo.DriverName);

                // ✅ Config'den MaxTagsPerClient ayarını al
                var maxTagsPerClient = driverInfo.ConnectionSettings.MaxTagsPerSubscription;
                _logger.LogInformation("📊 Client Pool Config: MaxTagsPerClient={MaxTags}, PublishingInterval={Interval}ms",
                    maxTagsPerClient, driverInfo.ConnectionSettings.PublishingInterval);

                // Tag'leri yükle
                var allTags = await LoadDriverTagsAsync(driverInfo.DriverId);
                _logger.LogInformation("📊 KEPServerEX toplam tag: {TagCount}", allTags.Count);

                // Tag'leri client'lara böl - config'den gelen limit ile
                await DistributeAndStartClientsAsync(allTags, driverInfo, maxTagsPerClient);

                _logger.LogInformation("✅ KEPServerEX Client Pool hazır: {ClientCount} client", _clients.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX Client Pool başlatılamadı");
                throw;
            }
        }
        private async Task DistributeAndStartClientsAsync(List<OpcTagInfo> allTags, OpcDriverInfo driverInfo, int maxTagsPerClient)
        {
            var clientTasks = new List<Task>();
            var clientId = 1;

            // Tag'leri config'den gelen limit ile gruplara böl
            for (int i = 0; i < allTags.Count; i += maxTagsPerClient)
            {
                var tagGroup = allTags.Skip(i).Take(maxTagsPerClient).ToList();

                var task = CreateClientAsync(clientId, tagGroup, driverInfo);
                clientTasks.Add(task);

                // Mapping kaydet
                foreach (var tag in tagGroup)
                {
                    _tagToClientMapping.TryAdd(tag.NodeId, clientId);
                }

                clientId++;
            }

            // Tüm client'ları paralel başlat
            await Task.WhenAll(clientTasks);

            _logger.LogInformation("🎯 Client dağıtımı tamamlandı: {ClientCount} client, Tag/Client: {TagsPerClient}",
                clientTasks.Count, maxTagsPerClient);
        }
        private async Task DistributeAndStartClientsAsync(List<OpcTagInfo> allTags, OpcDriverInfo driverInfo)
        {
            var clientTasks = new List<Task>();
            var clientId = 1;

            // Tag'leri 20,000'lik gruplara böl
            for (int i = 0; i < allTags.Count; i += _maxTagsPerClient)
            {
                var tagGroup = allTags.Skip(i).Take(_maxTagsPerClient).ToList();

                var task = CreateClientAsync(clientId, tagGroup, driverInfo);
                clientTasks.Add(task);

                // Mapping kaydet
                foreach (var tag in tagGroup)
                {
                    _tagToClientMapping.TryAdd(tag.NodeId, clientId);
                }

                clientId++;
            }

            // Tüm client'ları paralel başlat
            await Task.WhenAll(clientTasks);
        }

        private async Task CreateClientAsync(int clientId, List<OpcTagInfo> tags, OpcDriverInfo driverInfo)
        {
            try
            {
                _logger.LogInformation("🔧 KEPServerEX Client-{ClientId} oluşturuluyor: {TagCount} tag", clientId, tags.Count);

                var clientLogger = _loggerFactory.CreateLogger<OpcDriverClient>();

                var client = new OpcDriverClient(
                    $"KEPServer-{driverInfo.DriverId}-{clientId}",
                    driverInfo,
                    _dbManager,
                    _limits,
                    clientLogger);

                // Event'leri bağla
                client.DataChanged += OnClientDataChanged;
                client.StatusChanged += OnClientStatusChanged;

                // Client'ı başlat (mevcut StartAsync metodunu kullan)
                await client.StartAsync(tags);

                _clients.TryAdd(clientId, client);

                _logger.LogInformation("✅ KEPServerEX Client-{ClientId} hazır", clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ KEPServerEX Client-{ClientId} oluşturulamadı", clientId);
                throw;
            }
        }

        // Event handlers
        private void OnClientDataChanged(object? sender, OpcDataChangedEventArgs e)
        {
            // Queue service'e gönder (sonra ekleyeceğiz)
            _ = Task.Run(async () =>
            {
                await ProcessDataAsync(e);
            });
        }

        private void OnClientStatusChanged(object? sender, OpcStatusChangedEventArgs e)
        {
            _logger.LogInformation("📡 KEPServerEX Client durumu: {DriverName} -> {Status}",
                e.DriverName, e.Status);
        }

        private async Task ProcessDataAsync(OpcDataChangedEventArgs e)
        {
            // Şimdilik mevcut DataProcessor'ı kullan
            // Sonra Queue Service ekleyeceğiz
        }

        // API Methods - Tag ekleme/silme için
        public async Task<bool> AddTagAsync(OpcTagInfo newTag)
        {
            try
            {
                // En az yüklü client'ı bul
                var targetClientId = FindLeastLoadedClient();

                if (_clients.TryGetValue(targetClientId, out var client))
                {
                    await client.AddTagAsync(newTag);
                    _tagToClientMapping.TryAdd(newTag.NodeId, targetClientId);

                    _logger.LogInformation("✅ Tag eklendi: {TagName} -> Client-{ClientId}",
                        newTag.TagName, targetClientId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Tag eklenemedi: {TagName}", newTag.TagName);
                return false;
            }
        }

        public async Task<bool> RemoveTagAsync(string nodeId)
        {
            try
            {
                if (_tagToClientMapping.TryRemove(nodeId, out var clientId))
                {
                    if (_clients.TryGetValue(clientId, out var client))
                    {
                        await client.UnsubscribeTagAsync(nodeId);
                        _logger.LogInformation("✅ Tag silindi: {NodeId} <- Client-{ClientId}",
                            nodeId, clientId);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Tag silinemedi: {NodeId}", nodeId);
                return false;
            }
        }

        private int FindLeastLoadedClient()
        {
            return _clients
                .OrderBy(kvp => kvp.Value.GetTagCount())
                .FirstOrDefault().Key;
        }

        private async Task<List<OpcTagInfo>> LoadDriverTagsAsync(int driverId)
        {
            // Worker.cs'deki aynı SQL sorgusu
            const string sql = @"
                SELECT 
                    dtt.id as TagId,
                    cd.id as DeviceId,
                    cd.channelName as ChannelName,
                    CONCAT('Device_', cd.id) as DeviceName,
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
                    CONCAT('Device_', cd.id) as DeviceName,
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

            var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = driverId });

            var tags = new List<OpcTagInfo>();
            foreach (var result in results)
            {
                if (string.IsNullOrEmpty(result.TagName)) continue;

                string nodeId = $"ns=2;s={result.ChannelName}.{result.DeviceName}.{result.TagName}";

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

            return tags;
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    client?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Client dispose error");
                }
            }

            _clients.Clear();
            _tagToClientMapping.Clear();
        }
    }
}