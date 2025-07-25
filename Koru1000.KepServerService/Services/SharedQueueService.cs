// Koru1000.KepServerService/Services/SharedQueueService.cs
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Text;
using System.Globalization;
using Koru1000.Core.Models.OpcModels;

namespace Koru1000.KepServerService.Services
{
    public interface ISharedQueueService
    {
        Task EnqueueDataAsync(OpcDataChangedEventArgs data);
        Task StartAsync();
        Task StopAsync();
        Task<QueueStatistics> GetStatisticsAsync();
    }

    public class SharedQueueService : ISharedQueueService, IDisposable
    {
        private readonly Channel<OpcDataChangedEventArgs> _dataChannel;
        private readonly ChannelWriter<OpcDataChangedEventArgs> _writer;
        private readonly ChannelReader<OpcDataChangedEventArgs> _reader;

        private readonly DatabaseManager.DatabaseManager _dbManager;
        private readonly ILogger<SharedQueueService> _logger;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task? _processingTask;

        // Statistics
        private long _totalReceived;
        private long _totalProcessed;
        private long _totalErrors;
        private DateTime _lastProcessTime;

        // Database Connection Pool
        private readonly SemaphoreSlim _dbSemaphore;
        private const int MAX_DB_CONNECTIONS = 10;

        // Batch Processing
        private const int BATCH_SIZE = 5000;  // 5000 values per batch
        private const int BATCH_TIMEOUT_MS = 500; // 500ms max wait

        private volatile bool _isRunning;

        public SharedQueueService(
            DatabaseManager.DatabaseManager dbManager,
            ILogger<SharedQueueService> logger)
        {
            _dbManager = dbManager;
            _logger = logger;
            _cancellationTokenSource = new CancellationTokenSource();
            _dbSemaphore = new SemaphoreSlim(MAX_DB_CONNECTIONS, MAX_DB_CONNECTIONS);

            // High-performance channel - 100K capacity
            var channelOptions = new BoundedChannelOptions(100000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };

            _dataChannel = Channel.CreateBounded<OpcDataChangedEventArgs>(channelOptions);
            _writer = _dataChannel.Writer;
            _reader = _dataChannel.Reader;
        }

        public async Task StartAsync()
        {
            try
            {
                _logger.LogInformation("🚀 Starting Shared Queue Service...");
                _isRunning = true;

                // Background processing task başlat
                _processingTask = ProcessQueueAsync(_cancellationTokenSource.Token);

                _logger.LogInformation("✅ Shared Queue Service started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to start Shared Queue Service");
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _logger.LogInformation("🛑 Stopping Shared Queue Service...");
                _isRunning = false;

                // Writer'ı kapat
                _writer.Complete();

                // Processing task'ını durdur
                _cancellationTokenSource.Cancel();

                if (_processingTask != null)
                {
                    await _processingTask;
                }

                _logger.LogInformation("✅ Shared Queue Service stopped. Final stats - Received: {Received}, Processed: {Processed}, Errors: {Errors}",
                    _totalReceived, _totalProcessed, _totalErrors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error stopping Shared Queue Service");
            }
        }

        public async Task EnqueueDataAsync(OpcDataChangedEventArgs data)
        {
            try
            {
                if (!_isRunning) return;

                Interlocked.Increment(ref _totalReceived);

                // Non-blocking write
                if (!await _writer.WaitToWriteAsync(_cancellationTokenSource.Token))
                {
                    _logger.LogWarning("⚠️ Queue closed, data dropped from driver: {DriverName}", data.DriverName);
                    return;
                }

                await _writer.WriteAsync(data, _cancellationTokenSource.Token);

                // Performance log - Her 10K mesajda bir
                if (_totalReceived % 10000 == 0)
                {
                    _logger.LogInformation("📊 Queue Performance: {Received} received, {Processed} processed, Queue size: ~{QueueSize}",
                        _totalReceived, _totalProcessed, _reader.CanCount ? _reader.Count : -1);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to enqueue data from driver: {DriverName}", data.DriverName);
                Interlocked.Increment(ref _totalErrors);
            }
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("🔄 Queue processing started - Batch size: {BatchSize}, Timeout: {TimeoutMs}ms",
                    BATCH_SIZE, BATCH_TIMEOUT_MS);

                var batch = new List<TagValueForDb>();
                var lastBatchTime = DateTime.Now;

                await foreach (var dataEvent in _reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // DataEvent'teki tüm tag value'ları batch'e ekle
                        foreach (var tagValue in dataEvent.TagValues)
                        {
                            if (tagValue.Value != null && !string.IsNullOrEmpty(tagValue.TagName))
                            {
                                batch.Add(new TagValueForDb
                                {
                                    DeviceId = tagValue.DeviceId,
                                    TagName = tagValue.TagName,
                                    TagValue = ConvertValueToDouble(tagValue.Value),
                                    ReadTime = tagValue.SourceTimestamp != DateTime.MinValue ?
                                              tagValue.SourceTimestamp : DateTime.Now
                                });
                            }
                        }

                        // Batch dolu olduğunda veya timeout olduğunda işle
                        var timeSinceLastBatch = DateTime.Now - lastBatchTime;

                        if (batch.Count >= BATCH_SIZE ||
                            (batch.Any() && timeSinceLastBatch.TotalMilliseconds >= BATCH_TIMEOUT_MS))
                        {
                            await ProcessBatchAsync(batch);
                            batch.Clear();
                            lastBatchTime = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error processing data event from driver: {DriverName}", dataEvent.DriverName);
                        Interlocked.Increment(ref _totalErrors);
                    }
                }

                // Kalan batch'i işle
                if (batch.Any())
                {
                    await ProcessBatchAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("📝 Queue processing cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Queue processing error");
            }
        }

        private async Task ProcessBatchAsync(List<TagValueForDb> batch)
        {
            if (!batch.Any()) return;

            // Database connection limit
            await _dbSemaphore.WaitAsync();

            try
            {
                // Mevcut sistem gibi StringBuilder ile bulk insert
                var textForWrite = new StringBuilder();
                textForWrite.Append("CALL dbdataexchanger.sp_setTagValueOnDataChanged(\"");

                foreach (var tagValue in batch)
                {
                    try
                    {
                        textForWrite.Append($"({tagValue.DeviceId},'{tagValue.TagName}',{tagValue.TagValue.ToString("f6", CultureInfo.InvariantCulture)}),");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error formatting tag value: {TagName}", tagValue.TagName);
                    }
                }

                if (textForWrite.Length > 50) // En az bir değer varsa
                {
                    // Son virgülü kaldır
                    textForWrite.Remove(textForWrite.Length - 1, 1);
                    textForWrite.Append("\")");

                    // Stored procedure'u çalıştır
                    await _dbManager.ExecuteKbinAsync(textForWrite.ToString());

                    var processedCount = batch.Count;
                    Interlocked.Add(ref _totalProcessed, processedCount);
                    _lastProcessTime = DateTime.Now;

                    // Debug log - İlk birkaç batch'i göster
                    if (_totalProcessed <= 5000)
                    {
                        _logger.LogInformation("💾 Database batch written: {BatchSize} values, Total: {TotalProcessed}",
                            processedCount, _totalProcessed);
                    }

                    // Performance log - Her 50K değerde bir
                    if (_totalProcessed % 50000 == 0)
                    {
                        _logger.LogInformation("📊 Database Performance: {TotalProcessed} values processed, Last batch: {BatchSize}",
                            _totalProcessed, processedCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Database batch write failed - Batch size: {BatchSize}", batch.Count);
                Interlocked.Increment(ref _totalErrors);

                // Retry logic eklenebilir burada
                throw;
            }
            finally
            {
                _dbSemaphore.Release();
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
                    string str when double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double result) => result,
                    _ => 0.0
                };
            }
            catch
            {
                return 0.0;
            }
        }

        public async Task<QueueStatistics> GetStatisticsAsync()
        {
            return await Task.FromResult(new QueueStatistics
            {
                TotalReceived = _totalReceived,
                TotalProcessed = _totalProcessed,
                TotalErrors = _totalErrors,
                LastProcessTime = _lastProcessTime,
                IsRunning = _isRunning,
                QueueSize = _reader.CanCount ? _reader.Count : -1,
                DatabaseConnections = MAX_DB_CONNECTIONS - _dbSemaphore.CurrentCount
            });
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _dataChannel?.Writer.Complete();
            _dbSemaphore?.Dispose();
        }
    }

    // Helper classes
    public class TagValueForDb
    {
        public int DeviceId { get; set; }
        public string TagName { get; set; } = "";
        public double TagValue { get; set; }
        public DateTime ReadTime { get; set; }
    }

    public class QueueStatistics
    {
        public long TotalReceived { get; set; }
        public long TotalProcessed { get; set; }
        public long TotalErrors { get; set; }
        public DateTime LastProcessTime { get; set; }
        public bool IsRunning { get; set; }
        public int QueueSize { get; set; }
        public int DatabaseConnections { get; set; }
    }
}