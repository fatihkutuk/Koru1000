using Koru1000.Core.Models.OpcModels;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Koru1000.OpcService.Services
{
    public class OpcDataProcessor : IOpcDataProcessor
    {
        private readonly Koru1000.DatabaseManager.DatabaseManager _dbManager;
        private readonly OpcServiceConfig _config;
        private readonly ILogger<OpcDataProcessor> _logger;

        // Data processing queue
        private readonly Channel<OpcDataChangedEventArgs> _dataChannel;
        private readonly ChannelWriter<OpcDataChangedEventArgs> _dataWriter;
        private readonly ChannelReader<OpcDataChangedEventArgs> _dataReader;

        // Status processing queue
        private readonly Channel<OpcStatusChangedEventArgs> _statusChannel;
        private readonly ChannelWriter<OpcStatusChangedEventArgs> _statusWriter;
        private readonly ChannelReader<OpcStatusChangedEventArgs> _statusReader;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task? _dataProcessingTask;
        private Task? _statusProcessingTask;
        private bool _isRunning;

        // Statistics
        private long _totalDataReceived;
        private long _totalDataProcessed;
        private long _totalDataErrors;
        private DateTime _lastProcessTime;

        public OpcDataProcessor(
            Koru1000.DatabaseManager.DatabaseManager dbManager,
            OpcServiceConfig config,
            ILogger<OpcDataProcessor> logger)
        {
            _dbManager = dbManager;
            _config = config;
            _logger = logger;
            _cancellationTokenSource = new CancellationTokenSource();

            // Data processing channel (bounded channel for memory management)
            var dataChannelOptions = new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            _dataChannel = Channel.CreateBounded<OpcDataChangedEventArgs>(dataChannelOptions);
            _dataWriter = _dataChannel.Writer;
            _dataReader = _dataChannel.Reader;

            // Status processing channel
            var statusChannelOptions = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };
            _statusChannel = Channel.CreateBounded<OpcStatusChangedEventArgs>(statusChannelOptions);
            _statusWriter = _statusChannel.Writer;
            _statusReader = _statusChannel.Reader;
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation("OPC Data Processor başlatılıyor...");
                _isRunning = true;

                // Background processing task'larını başlat
                _dataProcessingTask = ProcessDataQueueAsync(_cancellationTokenSource.Token);
                _statusProcessingTask = ProcessStatusQueueAsync(_cancellationTokenSource.Token);

                _logger.LogInformation("OPC Data Processor başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC Data Processor başlatılamadı");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation("OPC Data Processor durduruluyor...");
                _isRunning = false;

                // Writer'ları kapat
                _dataWriter.Complete();
                _statusWriter.Complete();

                // Cancellation token'ı iptal et
                _cancellationTokenSource.Cancel();

                // Background task'ların bitmesini bekle
                if (_dataProcessingTask != null)
                    await _dataProcessingTask;

                if (_statusProcessingTask != null)
                    await _statusProcessingTask;

                _logger.LogInformation($"OPC Data Processor durduruldu. İstatistikler - Received: {_totalDataReceived}, Processed: {_totalDataProcessed}, Errors: {_totalDataErrors}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC Data Processor durdurulamadı");
            }
        }

        public async Task ProcessDataChangedAsync(OpcDataChangedEventArgs e)
        {
            try
            {
                Interlocked.Increment(ref _totalDataReceived);

                // Queue'ya ekle (non-blocking)
                if (!await _dataWriter.WaitToWriteAsync())
                {
                    _logger.LogWarning($"Data queue kapalı, veri atıldı - Driver: {e.DriverName}");
                    return;
                }

                await _dataWriter.WriteAsync(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Data queueing hatası - Driver: {e.DriverName}");
                Interlocked.Increment(ref _totalDataErrors);
            }
        }

        public async Task ProcessStatusChangedAsync(OpcStatusChangedEventArgs e)
        {
            try
            {
                // Queue'ya ekle (non-blocking)
                if (!await _statusWriter.WaitToWriteAsync())
                {
                    _logger.LogWarning($"Status queue kapalı, status atıldı - Driver: {e.DriverName}");
                    return;
                }

                await _statusWriter.WriteAsync(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Status queueing hatası - Driver: {e.DriverName}");
            }
        }

        private async Task ProcessDataQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Data processing task başlatıldı");

                var batch = new List<OpcDataChangedEventArgs>();
                const int batchSize = 100;
                var batchTimeout = TimeSpan.FromMilliseconds(500);

                await foreach (var dataEvent in _dataReader.ReadAllAsync(cancellationToken))
                {
                    batch.Add(dataEvent);

                    // Batch dolu olduğunda veya timeout olduğunda işle
                    if (batch.Count >= batchSize)
                    {
                        await ProcessDataBatchAsync(batch);
                        batch.Clear();
                    }
                    else
                    {
                        // Timeout kontrolü için kısa bekle
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(batchTimeout);

                        try
                        {
                            // Sonraki veriyi bekle veya timeout olsun
                            if (await _dataReader.WaitToReadAsync(timeoutCts.Token) && batch.Any())
                            {
                                continue; // Daha fazla veri var, batch'i büyütmeye devam et
                            }
                        }
                        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                        {
                            // Timeout oldu, mevcut batch'i işle
                            if (batch.Any())
                            {
                                await ProcessDataBatchAsync(batch);
                                batch.Clear();
                            }
                        }
                    }
                }

                // Kalan verileri işle
                if (batch.Any())
                {
                    await ProcessDataBatchAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Data processing task iptal edildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data processing task hatası");
            }
        }

        private async Task ProcessDataBatchAsync(List<OpcDataChangedEventArgs> batch)
        {
            try
            {
                var tagValues = new List<(int DeviceId, string TagName, object Value, DateTime ReadTime)>();

                foreach (var dataEvent in batch)
                {
                    foreach (var tagValue in dataEvent.TagValues)
                    {
                        // Sadece geçerli değerleri ekle
                        if (tagValue.Value != null && !string.IsNullOrEmpty(tagValue.TagName))
                        {
                            tagValues.Add((
                                tagValue.DeviceId,
                                tagValue.TagName,
                                tagValue.Value,
                                tagValue.SourceTimestamp != DateTime.MinValue ? tagValue.SourceTimestamp : DateTime.Now
                            ));
                        }
                    }
                }

                if (tagValues.Any())
                {
                    await WriteToTagOkuAsync(tagValues);
                    Interlocked.Add(ref _totalDataProcessed, tagValues.Count);
                    _lastProcessTime = DateTime.Now;

                    // DEBUG LOG - İlk birkaç batch'i göster
                    if (_totalDataProcessed <= 500)
                    {
                        _logger.LogInformation($"💾 DB WRITE: Processed batch with {tagValues.Count} tag values. Total processed: {_totalDataProcessed}");
                    }

                    if (_config.Logging.LogDataChanges)
                    {
                        _logger.LogDebug($"Processed batch: {batch.Count} events, {tagValues.Count} tag values");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Batch processing hatası - Batch size: {batch.Count}");
                Interlocked.Increment(ref _totalDataErrors);
            }
        }

        private async Task WriteToTagOkuAsync(List<(int DeviceId, string TagName, object Value, DateTime ReadTime)> tagValues)
        {
            try
            {
                // ESKİ KOD GİBİ - StringBuilder ile bulk insert
                var textForWrite = new StringBuilder();
                textForWrite.Append("CALL dbdataexchanger.sp_setTagValueOnDataChanged(\"");

                foreach (var (DeviceId, TagName, Value, ReadTime) in tagValues)
                {
                    try
                    {
                        double doubleValue = ConvertValueToDouble(Value);
                        textForWrite.Append($"({DeviceId},'{TagName}',{doubleValue.ToString("f6", CultureInfo.InvariantCulture)}),");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error converting value for tag {TagName}");
                    }
                }

                if (textForWrite.Length > 50) // En az bir değer varsa
                {
                    // Son virgülü kaldır
                    textForWrite.Remove(textForWrite.Length - 1, 1);
                    textForWrite.Append("\")");

                    // Stored procedure'u çalıştır
                    await _dbManager.ExecuteKbinAsync(textForWrite.ToString());

                    _logger.LogDebug($"Bulk inserted {tagValues.Count} tag values using stored procedure");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Bulk tag insert failed - {tagValues.Count} values");
                throw;
            }
        }

        private double ConvertValueToDouble(object value)
        {
            try
            {
                return value switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    short s => s,
                    byte b => b,
                    bool bl => bl ? 1.0 : 0.0,
                    string str when double.TryParse(str, out double result) => result,
                    _ => 0.0
                };
            }
            catch
            {
                return 0.0;
            }
        }

        private async Task ProcessStatusQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Status processing task başlatıldı");

                await foreach (var statusEvent in _statusReader.ReadAllAsync(cancellationToken))
                {
                    await ProcessSingleStatusAsync(statusEvent);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Status processing task iptal edildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Status processing task hatası");
            }
        }

        private async Task ProcessSingleStatusAsync(OpcStatusChangedEventArgs statusEvent)
        {
            try
            {
                if (_config.Logging.LogConnectionStatus)
                {
                    _logger.LogInformation($"Driver {statusEvent.DriverName} ({statusEvent.DriverId}) status: {statusEvent.Status} - {statusEvent.Message}");
                }

                // Status değişikliklerini veritabanına kaydet (opsiyonel)
                await LogStatusToDatabase(statusEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Status processing hatası - Driver: {statusEvent.DriverName}");
            }
        }

        private async Task LogStatusToDatabase(OpcStatusChangedEventArgs statusEvent)
        {
            try
            {
                // ChannelDevice tablosunda statusCode'u güncelle
                const string updateSql = @"
                    UPDATE channeldevice cd
                    INNER JOIN driver_channeltype_relation dcr ON dcr.channelTypeId IN (
                        SELECT dt.ChannelTypeId FROM devicetype dt WHERE dt.id = cd.deviceTypeId
                    )
                    SET cd.statusCode = @StatusCode, cd.updateTime = NOW()
                    WHERE dcr.driverId = @DriverId";

                int statusCode = statusEvent.Status switch
                {
                    OpcConnectionStatus.Connected => 41, // Running
                    OpcConnectionStatus.Connecting => 31, // Connected
                    OpcConnectionStatus.Reconnecting => 31, // Connected
                    OpcConnectionStatus.Disconnected => 51, // Stopped
                    OpcConnectionStatus.Error => 50, // Error
                    _ => 50
                };

                await _dbManager.ExecuteExchangerAsync(updateSql, new
                {
                    StatusCode = statusCode,
                    DriverId = statusEvent.DriverId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Status database update hatası - Driver: {statusEvent.DriverName}");
            }
        }

        public async Task<ProcessorStatistics> GetStatisticsAsync()
        {
            return await Task.FromResult(new ProcessorStatistics
            {
                TotalDataReceived = _totalDataReceived,
                TotalDataProcessed = _totalDataProcessed,
                TotalDataErrors = _totalDataErrors,
                LastProcessTime = _lastProcessTime,
                IsRunning = _isRunning,
                DataQueueCount = _dataReader.CanCount ? _dataReader.Count : -1,
                StatusQueueCount = _statusReader.CanCount ? _statusReader.Count : -1
            });
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _dataChannel?.Writer.Complete();
            _statusChannel?.Writer.Complete();
        }
    }

    // Statistics model
    public class ProcessorStatistics
    {
        public long TotalDataReceived { get; set; }
        public long TotalDataProcessed { get; set; }
        public long TotalDataErrors { get; set; }
        public DateTime LastProcessTime { get; set; }
        public bool IsRunning { get; set; }
        public int DataQueueCount { get; set; }
        public int StatusQueueCount { get; set; }
    }
}