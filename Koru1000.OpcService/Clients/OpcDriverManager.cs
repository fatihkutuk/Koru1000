// Koru1000.OpcService/Clients/OpcDriverManager.cs
using Koru1000.Core.Models.OpcModels;
using System.Collections.Concurrent;

namespace Koru1000.OpcService.Clients
{
    public class OpcDriverManager : IDisposable
    {
        private readonly int _driverId;
        private readonly OpcDriverInfo _driverInfo;
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly ClientLimits _limits;
        private readonly ILogger<OpcDriverManager> _logger;
        private readonly ILoggerFactory _loggerFactory;

        private readonly ConcurrentDictionary<int, OpcClient> _clients;
        private readonly Timer _statusTimer;
        private bool _isRunning;

        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public string DriverName => _driverInfo.DriverName;
        public int DriverId => _driverId;

        public OpcDriverManager(
            int driverId,
            OpcDriverInfo driverInfo,
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            ClientLimits limits,
            ILogger<OpcDriverManager> logger,
            ILoggerFactory loggerFactory)
        {
            _driverId = driverId;
            _driverInfo = driverInfo;
            _dbManager = dbManager;
            _limits = limits;
            _logger = logger;
            _loggerFactory = loggerFactory;

            _clients = new ConcurrentDictionary<int, OpcClient>();
            _statusTimer = new Timer(StatusCheck, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation($"🚀 === STARTING DRIVER MANAGER: {_driverInfo.DriverName} ===");
                _isRunning = true;

                var allTags = await LoadDriverTagsAsync();
                if (!allTags.Any())
                {
                    _logger.LogWarning($"⚠️ No tags found for driver: {_driverInfo.DriverName}");
                    return;
                }

                _logger.LogInformation($"📊 Found {allTags.Count} total tags for driver {_driverInfo.DriverName}");

                // 20000'ERLİ CLIENT'LAR OLUŞTUR
                int maxTagsPerClient = 20000;

                var clientGroups = allTags
                    .Select((tag, index) => new { tag, index })
                    .GroupBy(x => x.index / maxTagsPerClient)
                    .Select(g => g.Select(x => x.tag).ToList())
                    .ToList();

                _logger.LogInformation($"📋 Creating {clientGroups.Count} clients with max {maxTagsPerClient} tags each");

                // SERİ OLARAK CLIENT OLUŞTUR (timeout olmasın diye)
                for (int i = 0; i < clientGroups.Count; i++)
                {
                    var clientId = i + 1;
                    var tags = clientGroups[i];

                    var client = new OpcClient(
                        clientId,
                        _driverInfo,
                        tags,
                        _dbManager,
                        _limits,
                        _loggerFactory.CreateLogger<OpcClient>());

                    client.DataChanged += OnClientDataChanged;
                    client.StatusChanged += OnClientStatusChanged;

                    _clients.TryAdd(clientId, client);

                    // SERİ BAŞLAT
                    await client.StartAsync();

                    _logger.LogInformation($"✅ Started client {clientId} with {tags.Count} tags");

                    // CLIENT'LAR ARASINDA 3 SANİYE BEKLE
                    await Task.Delay(3000);
                }

                _logger.LogInformation($"✅ === DRIVER MANAGER STARTED: {_driverInfo.DriverName} with {_clients.Count} clients ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Failed to start driver manager: {_driverInfo.DriverName}");
                throw;
            }
        }

        private async Task StartClientAsync(OpcClient client, int clientId, int tagCount)
        {
            try
            {
                await client.StartAsync();
                _logger.LogInformation($"✅ Started client {clientId} with {tagCount} tags");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Failed to start client {clientId}");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation($"🛑 === STOPPING DRIVER MANAGER: {_driverInfo.DriverName} ===");
                _isRunning = false;

                _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

                var stopTasks = _clients.Values.Select(client => client.StopAsync());
                await Task.WhenAll(stopTasks);

                foreach (var client in _clients.Values)
                {
                    client.Dispose();
                }
                _clients.Clear();

                _logger.LogInformation($"✅ === DRIVER MANAGER STOPPED: {_driverInfo.DriverName} ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Error stopping driver manager: {_driverInfo.DriverName}");
            }
        }

        private async Task<List<OpcTagInfo>> LoadDriverTagsAsync()
        {
            try
            {
                _logger.LogInformation($"📋 === LOADING TAGS FOR DRIVER {_driverId} ===");

                // BASİT SQL - cd.driverId ile direkt çek
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
            WHERE cd.driverId = @DriverId AND cd.statusCode IN (11,31,41,61)
            
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
            INNER JOIN deviceindividualtag dit ON dit.channelDeviceId = cd.id
            WHERE cd.driverId = @DriverId AND cd.statusCode IN (11,31,41,61)
            ORDER BY DeviceId, TagName";

                var results = await _dbManager.QueryExchangerAsync<dynamic>(sql, new { DriverId = _driverId });

                var tags = new List<OpcTagInfo>();
                foreach (var result in results)
                {
                    if (string.IsNullOrEmpty(result.TagName))
                        continue;

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

                _logger.LogInformation($"✅ Loaded {tags.Count} tags for driver {_driverId}");
                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Failed to load tags for driver {_driverId}");
                return new List<OpcTagInfo>();
            }
        }

        private void OnClientDataChanged(object? sender, OpcDataChangedEventArgs e)
        {
            DataChanged?.Invoke(sender, e);
        }

        private void OnClientStatusChanged(object? sender, OpcStatusChangedEventArgs e)
        {
            StatusChanged?.Invoke(sender, e);
        }

        private void StatusCheck(object? state)
        {
            if (!_isRunning) return;

            foreach (var client in _clients.Values)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.CheckConnectionAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"💥 Client {client.ClientId} status check failed");
                    }
                });
            }
        }

        public async Task<List<OpcServiceStatus>> GetStatusAsync()
        {
            var statusList = new List<OpcServiceStatus>();

            foreach (var client in _clients.Values)
            {
                var status = await client.GetStatusAsync();
                statusList.Add(status);
            }

            return statusList;
        }

        public void Dispose()
        {
            _statusTimer?.Dispose();

            foreach (var client in _clients.Values)
            {
                try
                {
                    client.Dispose();
                }
                catch { }
            }

            _clients.Clear();
        }
    }
}