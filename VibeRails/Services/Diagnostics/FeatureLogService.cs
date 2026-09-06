using System.Threading.Channels;
using VibeRails.DTOs;

namespace VibeRails.Services.Diagnostics;

/// <summary>
/// Bounded, best-effort feature logging. Nothing touches the filesystem until an event is
/// consumed in the background or the internal tools UI explicitly requests a page.
/// </summary>
public sealed class FeatureLogService : IFeatureLog, IFeatureLogReader, IHostedService, IAsyncDisposable
{
    private readonly Channel<InternalLogEntry> _queue;
    private readonly FeatureLogFileStore _store;
    private readonly FeatureLogOptions _options;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _writerCancellation = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private Task? _writerTask;
    private FeatureLogSnapshot? _cached;
    private long _cachedAt;
    private long _entrySequence;
    private long _droppedCount;
    private long _writeFailures;
    private int _shutdownInterrupted;

    public FeatureLogService() : this(new FeatureLogOptions()) { }

    public FeatureLogService(FeatureLogOptions options)
    {
        if (options.DirectoryPath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(options.DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSegmentBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetainedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxReadEntries, 1);
        _options = options;
        _store = new FeatureLogFileStore(options, _instanceId);
        _queue = Channel.CreateBounded<InternalLogEntry>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            // TryWrite reports a full queue; DropWrite would report success for discarded entries.
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Write(string feature, string eventName, string message, string? operationId = null,
        string? subject = null, string? status = null, LogLevel level = LogLevel.Information)
    {
        if (level == LogLevel.None)
            return;

        var entry = new InternalLogEntry(
            $"{_instanceId}-{Interlocked.Increment(ref _entrySequence):D12}",
            DateTimeOffset.UtcNow,
            Category(feature, "general").ToLowerInvariant(),
            level.ToString(), Category(eventName, "event"), Clip(message, 2048) ?? "",
            Clip(operationId, 128), Clip(subject, 512), Clip(status, 64));
        if (!_queue.Writer.TryWrite(entry))
            Interlocked.Increment(ref _droppedCount);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Task.Run also keeps directory setup/serialization off the caller on the first batch.
        _writerTask ??= Task.Run(WriteLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writerTask != null)
                await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _shutdownInterrupted, 1);
            _writerCancellation.Cancel();
            // A running writer reports what it abandons when it observes the cancellation. With no
            // writer nothing ever will, so the accepted events are accounted for here instead.
            if (_writerTask == null)
                Interlocked.Add(ref _droppedCount, DrainQueue());
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Do not resume an unbounded wait after the host already exhausted its shutdown budget.
        // A stuck filesystem operation may take time to observe cancellation in the background.
        if (Volatile.Read(ref _shutdownInterrupted) != 0)
            return;

        // Hosted StopAsync normally drained everything already. Direct users get a bounded
        // drain too, so an unavailable disk cannot hang DI/container disposal indefinitely.
        _queue.Writer.TryComplete();
        _writerTask ??= Task.Run(WriteLoopAsync, CancellationToken.None);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await StopAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<InternalLogEntry>(128);
        var cancellationToken = _writerCancellation.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce queued bursts; there is no idle polling or synchronous work on Write.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                while (batch.Count < 128 && _queue.Reader.TryRead(out var entry))
                    batch.Add(entry);
                try
                {
                    await _store.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Diagnostic persistence must never take down the upload or web host.
                    // Failures are surfaced in the UI; don't recursively log them via ILogger.
                    // Counted per event so the UI number is the number of missing events.
                    Interlocked.Add(ref _writeFailures, Unpersisted(batch));
                    await _store.CloseAfterFailureAsync(cancellationToken).ConfigureAwait(false);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An interrupted shutdown abandons events Write already accepted. Report them as
            // dropped so a gap in the journal is never mistaken for a quiet period.
            Interlocked.Add(ref _droppedCount, Unpersisted(batch) + DrainQueue());
        }
        finally
        {
            try { await _store.CloseAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception) { Interlocked.Increment(ref _writeFailures); }
        }
    }

    /// <summary>Entries of the batch in flight that never reached a sealed or flushed segment.</summary>
    private int Unpersisted(List<InternalLogEntry> batch) =>
        batch.Count == 0 ? 0 : batch.Count - _store.LastBatchPersisted;

    private long DrainQueue()
    {
        var count = 0L;
        while (_queue.Reader.TryRead(out _))
            count++;
        return count;
    }

    public async Task<InternalLogResponse> ReadAsync(FeatureLogQuery query, bool uploadsOnly = false,
        CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FeatureLogSnapshot snapshot;
        try
        {
            if (_cached == null || Environment.TickCount64 - _cachedAt >= _options.ReadCacheDuration.TotalMilliseconds)
            {
                _cached = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                _cachedAt = Environment.TickCount64;
            }
            snapshot = _cached;
        }
        finally { _readGate.Release(); }

        var entries = snapshot.Entries;
        IEnumerable<InternalLogEntry> filtered = entries;
        if (uploadsOnly)
        {
            // Select the current outcome first. Filtering failed/started events before grouping
            // would incorrectly resurrect an earlier state of an operation that later succeeded.
            filtered = filtered.Where(e => Matches(e.Feature, "data-upload") && !string.IsNullOrEmpty(e.OperationId))
                .DistinctBy(e => e.OperationId, StringComparer.OrdinalIgnoreCase);
        }
        var feature = Clip(query.Feature, 128)?.Trim();
        var level = Clip(query.Level, 32)?.Trim();
        var status = Clip(query.Status, 64)?.Trim();
        var operation = Clip(query.OperationId, 128)?.Trim();
        var search = Clip(query.Search, 256)?.Trim();
        filtered = filtered.Where(e => Matches(e.Feature, feature) && Matches(e.Level, level) &&
            Matches(e.Status, status) && Matches(e.OperationId, operation) &&
            (string.IsNullOrEmpty(search) || Contains(e, search)));
        var limit = Math.Clamp(query.Limit, 1, 200);
        var page = filtered.Skip(Math.Clamp(query.Offset, 0, 10_000)).Take(limit + 1).ToArray();
        var features = entries.Select(e => e.Feature).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        // Dropped and write-failure counts cover the process lifetime; read failures describe the
        // snapshot this page came from, so re-reading the same damaged line never inflates them.
        return new InternalLogResponse(page.Take(limit).ToArray(), features, page.Length > limit,
            Interlocked.Read(ref _droppedCount), Interlocked.Read(ref _writeFailures),
            snapshot.ReadFailures);
    }

    private static bool Matches(string? value, string? filter) => string.IsNullOrEmpty(filter) ||
        string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(InternalLogEntry entry, string search) =>
        entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.EventName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.Feature.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (entry.Subject?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (entry.OperationId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? Clip(string? value, int maxLength) =>
        value?.Length > maxLength ? value[..maxLength] : value;

    private static string Category(string? value, string fallback) =>
        Clip(value, 128)?.Trim() is { Length: > 0 } clipped ? clipped : fallback;
}
