using Koru1000.Core.Models.OpcModels;
using Koru1000.OpcService.Clients;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Koru1000.OpcService.Services
{
    public class OpcClientManager : IOpcClientManager
    {
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly OpcServiceConfig _config;
        private readonly ILogger<OpcClientManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<int, OpcDriverManager> _driverManagers; // DEĞİŞTİ
        private readonly Timer _statusTimer;
        private readonly IOpcDataProcessor _dataProcessor;
        private bool _isRunning;

        public event EventHandler<OpcDataChangedEventArgs>? DataChanged;
        public event EventHandler<OpcStatusChangedEventArgs>? StatusChanged;

        public OpcClientManager(
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            OpcServiceConfig config,
            IOpcDataProcessor dataProcessor,
            ILogger<OpcClientManager> logger,
            ILoggerFactory loggerFactory)
        {
            _dbManager = dbManager;
            _config = config;
            _dataProcessor = dataProcessor;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _driverManagers = new ConcurrentDictionary<int, OpcDriverManager>(); // DEĞİŞTİ

            _statusTimer = new Timer(CheckConnectionStatus, null,
                TimeSpan.FromSeconds(_config.StatusCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.StatusCheckIntervalSeconds));
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation("OPC Client Manager başlatılıyor...");
                _isRunning = true;

                await LoadKepServerExDriversAsync();

                _logger.LogInformation($"OPC Client Manager başlatıldı. {_driverManagers.Count} driver manager aktif.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC Client Manager başlatılamadı");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation("OPC Client Manager durduruluyor...");
                _isRunning = false;

                _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);

                var stopTasks = _driverManagers.Values.Select(manager => manager.StopAsync());
                await Task.WhenAll(stopTasks);

                foreach (var manager in _driverManagers.Values)
                {
                    manager.Dispose();
                }
                _driverManagers.Clear();

                _logger.LogInformation("OPC Client Manager durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC Client Manager durdurulamadı");
            }
        }

        private async Task LoadKepServerExDriversAsync()
        {
            try
            {
                const string sql = @"
                    SELECT DISTINCT d.id, d.name, d.customSettings, dt.name as driverTypeName
                    FROM driver d
                    INNER JOIN drivertype dt ON d.driverTypeId = dt.id
                    INNER JOIN driver_channeltype_relation dcr ON d.id = dcr.driverId
                    WHERE dt.name = 'KEPSERVEREX'
                    ORDER BY d.id";

                var drivers = await _dbManager.QueryExchangerAsync<dynamic>(sql);
                _logger.LogInformation($"Found {drivers.Count()} KEPSERVEREX drivers to load");

                foreach (var driver in drivers)
                {
                    await CreateDriverManagerAsync(driver);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KEPSERVEREX driver'ları yüklenirken hata");
                throw;
            }
        }

        private async Task CreateDriverManagerAsync(dynamic driverData)
        {
            try
            {
                int driverId = (int)driverData.id;
                string driverName = driverData.name;

                // Custom settings parse et
                var customSettings = new Dictionary<string, object>();
                KepConnectionSettings connectionSettings = new();
                KepSecuritySettings securitySettings = new();
                KepCredentials credentials = new();
                string namespace_ = "2";
                string protocolType = "OPC";
                string addressFormat = "ns={namespace};s={channelName}.{deviceName}.{tagName}";

                try
                {
                    if (!string.IsNullOrEmpty(driverData.customSettings?.ToString()))
                    {
                        var jsonDoc = JsonDocument.Parse(driverData.customSettings.ToString());
                        var root = jsonDoc.RootElement;

                        customSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            driverData.customSettings.ToString()) ?? new Dictionary<string, object>();

                        // Parse settings
                        if (root.TryGetProperty("namespace", out JsonElement ns))
                            namespace_ = ns.GetString() ?? "2";

                        if (root.TryGetProperty("protocolType", out JsonElement pt))
                            protocolType = pt.GetString() ?? "OPC";

                        if (root.TryGetProperty("addressFormat", out JsonElement af))
                            addressFormat = af.GetString() ?? "ns={namespace};s={channelName}.{deviceName}.{tagName}";

                        if (root.TryGetProperty("connectionSettings", out JsonElement cs))
                        {
                            if (cs.TryGetProperty("updateRate", out JsonElement ur))
                                connectionSettings.UpdateRate = ur.GetInt32();
                            if (cs.TryGetProperty("groupDeadband", out JsonElement gd))
                                connectionSettings.GroupDeadband = gd.GetInt32();
                            if (cs.TryGetProperty("sessionTimeout", out JsonElement st))
                                connectionSettings.SessionTimeout = st.GetInt32();
                            if (cs.TryGetProperty("publishingInterval", out JsonElement pi))
                                connectionSettings.PublishingInterval = pi.GetInt32();
                        }

                        if (root.TryGetProperty("security", out JsonElement sec))
                        {
                            if (sec.TryGetProperty("mode", out JsonElement sm))
                                securitySettings.Mode = sm.GetString() ?? "SignAndEncrypt";
                            if (sec.TryGetProperty("policy", out JsonElement sp))
                                securitySettings.Policy = sp.GetString() ?? "Basic256Sha256";
                            if (sec.TryGetProperty("userTokenType", out JsonElement ut))
                                securitySettings.UserTokenType = ut.GetString() ?? "UserName";
                        }

                        if (root.TryGetProperty("credentials", out JsonElement cred))
                        {
                            if (cred.TryGetProperty("username", out JsonElement user))
                                credentials.Username = user.GetString() ?? "";
                            if (cred.TryGetProperty("password", out JsonElement pass))
                                credentials.Password = pass.GetString() ?? "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Custom settings parse edilemedi for driver {driverName}");
                }

                string endpointUrl = customSettings.GetValueOrDefault("EndpointUrl", "")?.ToString();
                if (string.IsNullOrEmpty(endpointUrl))
                {
                    endpointUrl = "opc.tcp://localhost:49320";
                    _logger.LogWarning($"Driver {driverName} için EndpointUrl bulunamadı, default kullanılıyor: {endpointUrl}");
                }

                var channelTypeIds = await GetDriverChannelTypeIdsAsync(driverId);

                var driverInfo = new OpcDriverInfo
                {
                    DriverId = driverId,
                    DriverName = driverName,
                    DriverType = "KEPSERVEREX",
                    EndpointUrl = endpointUrl,
                    IsEnabled = true,
                    CustomSettings = customSettings,
                    ChannelTypeIds = channelTypeIds,
                    Namespace = namespace_,
                    ProtocolType = protocolType,
                    AddressFormat = addressFormat,
                    ConnectionSettings = connectionSettings,
                    Security = securitySettings,
                    Credentials = credentials
                };

                // OpcDriverManager oluştur
                var driverManager = new OpcDriverManager(driverId, driverInfo, _dbManager, _config.Limits,
                    _loggerFactory.CreateLogger<OpcDriverManager>());

                driverManager.DataChanged += OnDriverDataChanged;
                driverManager.StatusChanged += OnDriverStatusChanged;

                await driverManager.StartAsync();

                _driverManagers.TryAdd(driverId, driverManager);
                _logger.LogInformation($"Driver manager oluşturuldu: {driverName} [{endpointUrl}]");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Driver manager oluşturulamadı: {driverData.name}");
            }
        }

        private async Task<List<int>> GetDriverChannelTypeIdsAsync(int driverId)
        {
            try
            {
                const string sql = @"
                    SELECT channelTypeId 
                    FROM driver_channeltype_relation 
                    WHERE driverId = @DriverId";

                var results = await _dbManager.QueryExchangerAsync<int>(sql, new { DriverId = driverId });
                return results.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Driver {driverId} için channel type ID'leri alınamadı");
                return new List<int>();
            }
        }

        private void OnDriverDataChanged(object? sender, OpcDataChangedEventArgs e)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dataProcessor.ProcessDataChangedAsync(e);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Data processing hatası - Driver: {e.DriverName}");
                    }
                });

                DataChanged?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Driver data changed event işlenirken hata: {e.DriverName}");
            }
        }

        private void OnDriverStatusChanged(object? sender, OpcStatusChangedEventArgs e)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dataProcessor.ProcessStatusChangedAsync(e);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Status processing hatası - Driver: {e.DriverName}");
                    }
                });

                StatusChanged?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Driver status changed event işlenirken hata: {e.DriverName}");
            }
        }

        private void CheckConnectionStatus(object? state)
        {
            if (!_isRunning) return;

            foreach (var manager in _driverManagers.Values)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Manager'ın status'unu kontrol et
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Driver manager status kontrolünde hata");
                    }
                });
            }
        }

        public async Task<List<OpcServiceStatus>> GetServiceStatusAsync()
        {
            var statusList = new List<OpcServiceStatus>();

            foreach (var manager in _driverManagers.Values)
            {
                var statuses = await manager.GetStatusAsync();
                statusList.AddRange(statuses);
            }

            return statusList;
        }

        public void Dispose()
        {
            _statusTimer?.Dispose();

            foreach (var manager in _driverManagers.Values)
            {
                try
                {
                    manager.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Driver manager dispose hatası");
                }
            }

            _driverManagers.Clear();
        }
    }
}