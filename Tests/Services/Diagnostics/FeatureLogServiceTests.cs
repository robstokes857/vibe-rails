using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Diagnostics;

public sealed class FeatureLogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "viberails-feature-log-tests", Guid.NewGuid().ToString("N"));
    private string LogDirectory => Path.Combine(_root, "logs");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AcceptedEventsPersistAcrossServiceRestart_WithFiltersAndPagination()
    {
        await using (var writer = new FeatureLogService(Options()))
        {
            await writer.StartAsync(Token);
            writer.Write("data-upload", "upload-started", "Started snapshot", "op-1", "Database snapshot", "started");
            writer.Write("data-upload", "upload-failed", "Remote server rejected snapshot", "op-1",
                "Database snapshot", "failed", LogLevel.Error);
            writer.Write("worker", "completed", "Finished work", "op-2", status: "succeeded");
            await writer.StopAsync(Token);
        }

        await using var reader = new FeatureLogService(Options());
        var all = await reader.ReadAsync(new FeatureLogQuery(Limit: 1), cancellationToken: Token);
        Assert.Single(all.Entries);
        Assert.Equal("worker", all.Entries[0].Feature);
        Assert.True(all.HasMore);
        Assert.Equal(["data-upload", "worker"], all.Features);
        Assert.Equal(0, all.WriteFailures);

        var filtered = await reader.ReadAsync(new FeatureLogQuery(Feature: "DATA-UPLOAD", Level: "error",
            Status: "FAILED", Search: "snapshot", OperationId: "OP-1"), cancellationToken: Token);
        Assert.Equal("upload-failed", Assert.Single(filtered.Entries).EventName);
        Assert.False(filtered.HasMore);
        var next = await reader.ReadAsync(new FeatureLogQuery(Offset: 1, Limit: 1), cancellationToken: Token);
        Assert.Equal("upload-failed", Assert.Single(next.Entries).EventName);
        Assert.True(next.HasMore);
    }

    [Fact]
    public async Task UploadsGroupCurrentOutcomeBeforeApplyingStatusAndSearchFilters()
    {
        await using var log = new FeatureLogService(Options());
        await log.StartAsync(Token);
        log.Write("data-upload", "started", "Preparing archive", "op-1", status: "started");
        log.Write("data-upload", "uploaded", "Remote accepted archive", "op-1", status: "uploaded");
        log.Write("data-upload", "failed", "Another archive failed", "op-2", status: "failed");
        log.Write("worker", "failed", "Unrelated worker", "op-3", status: "failed");
        await log.StopAsync(Token);

        var uploads = await log.ReadAsync(new FeatureLogQuery(), uploadsOnly: true, cancellationToken: Token);
        Assert.Equal(2, uploads.Entries.Count);
        Assert.Equal(["op-2", "op-1"], uploads.Entries.Select(e => e.OperationId));
        Assert.Empty((await log.ReadAsync(new FeatureLogQuery(Status: "started"), uploadsOnly: true,
            cancellationToken: Token)).Entries);
        Assert.Empty((await log.ReadAsync(new FeatureLogQuery(Search: "Preparing"), uploadsOnly: true,
            cancellationToken: Token)).Entries);
        Assert.Single((await log.ReadAsync(new FeatureLogQuery(Status: "failed"), uploadsOnly: true,
            cancellationToken: Token)).Entries);
    }

    [Fact]
    public async Task ProducerNeverWaitsForConsumer_AndReportsEveryOverflowWithoutTouchingDisk()
    {
        await using var log = new FeatureLogService(Options(queueCapacity: 3));
        // Deliberately leave the consumer stopped: a blocking Write would deadlock on entry 4.
        // This checks queue behavior without relying on timing or the machine's disk speed.
        for (var i = 0; i < 100; i++)
            log.Write("upload", "queued", $"Event {i}");

        Assert.False(Directory.Exists(LogDirectory));
        var pending = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
        Assert.Equal(97, pending.DroppedCount);
        Assert.Empty(pending.Entries);
        Assert.False(Directory.Exists(LogDirectory));

        await log.StartAsync(Token);
        await log.StopAsync(Token);
        var flushed = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
        Assert.Equal(3, flushed.Entries.Count);
        Assert.Equal(97, flushed.DroppedCount);
        Assert.Equal(0, flushed.WriteFailures);
    }

    [Fact]
    public async Task CancelledShutdownDoesNotResumeWaitingWhenContainerDisposesLogger()
    {
        var log = new FeatureLogService(Options());
        // Suspend the consumer entirely. Cancellation must close the producer and subsequent
        // disposal must not start a new writer to drain the accepted events behind the host.
        log.Write("data-upload", "queued", "Accepted before shutdown", "op-1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.StopAsync(cancelled.Token));
        var disposal = log.DisposeAsync();
        Assert.True(disposal.IsCompletedSuccessfully);
        await disposal;
        Assert.False(Directory.Exists(LogDirectory));
        // The accepted-but-abandoned event and the post-close write are both reported as dropped.
        Assert.Equal(1, (await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token)).DroppedCount);
        log.Write("data-upload", "too-late", "Closed producer", "op-2");
        Assert.Equal(2, (await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token)).DroppedCount);
    }

    [Fact]
    public async Task InterruptedShutdownWithRunningWriterAccountsForEveryAcceptedEvent()
    {
        // Nothing can ever be persisted here, so every accepted event must end up counted as
        // either dropped (writer cancelled before its batch) or failed (writer reached the disk).
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(LogDirectory, "A regular file prevents directory creation.", Token);
        var log = new FeatureLogService(Options());
        await log.StartAsync(Token);
        for (var i = 0; i < 5; i++)
            log.Write("data-upload", "queued", $"Accepted before shutdown {i}", "op-1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.StopAsync(cancelled.Token));
        await log.DisposeAsync();

        // The writer observes the cancellation in the background; the counters settle shortly after.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        InternalLogResponse response;
        while ((response = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token)).DroppedCount
               + response.WriteFailures < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(20, Token);
        Assert.Equal(5, response.DroppedCount + response.WriteFailures);
        Assert.Empty(response.Entries);
    }

    [Fact]
    public async Task DiskFailureCountsEveryLostEventWithoutEscapingWriterOrShutdown()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(LogDirectory, "A regular file prevents directory creation.", Token);
        await using var log = new FeatureLogService(Options());
        await log.StartAsync(Token);
        for (var i = 0; i < 3; i++)
            log.Write("data-upload", "succeeded", $"Upload {i} completed", $"op-{i}", status: "succeeded");
        await log.StopAsync(Token);

        var response = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
        // Events, not batches: the UI number is the number of missing events.
        Assert.Equal(3, response.WriteFailures);
        Assert.Equal(0, response.DroppedCount);
        Assert.Empty(response.Entries);
    }

    [Fact]
    public async Task ReaderSkipsCorruptAndPartialLines_WithoutLosingValidEvents()
    {
        await using (var writer = new FeatureLogService(Options()))
        {
            await writer.StartAsync(Token);
            writer.Write("data-upload", "succeeded", "Upload completed", "op-1");
            await writer.StopAsync(Token);
        }
        var path = Assert.Single(Directory.GetFiles(LogDirectory, "*.jsonl"));
        await File.AppendAllTextAsync(path, "{not-json}\nnull\n{\"id\":\"unfinished", Token);

        await using var reader = new FeatureLogService(Options());
        var response = await reader.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
        Assert.Equal("op-1", Assert.Single(response.Entries).OperationId);
        Assert.Equal(2, response.ReadFailures);
        // Refreshing over the same damaged lines describes the snapshot again; it does not accumulate.
        Assert.Equal(2, (await reader.ReadAsync(new FeatureLogQuery(), cancellationToken: Token)).ReadFailures);
    }

    [Fact]
    public async Task RotationRetainsOnlyBoundedNewestSegments()
    {
        await using var log = new FeatureLogService(Options(segmentBytes: 1024, retainedFiles: 3));
        await log.StartAsync(Token);
        for (var i = 0; i < 20; i++)
            log.Write("upload", $"event-{i}", new string('x', 400));
        await log.StopAsync(Token);

        var files = Directory.GetFiles(LogDirectory, "*.jsonl");
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        var response = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
        Assert.Equal(["event-19", "event-18", "event-17"], response.Entries.Select(e => e.EventName));
    }

    [Fact]
    public async Task RotationDoesNotDeleteAnotherWriterActiveSegment_AndReaderSeesBothInstances()
    {
        var options = Options(segmentBytes: 1024, retainedFiles: 3);
        var otherWriter = new FeatureLogFileStore(options, Guid.NewGuid().ToString("N"));
        await otherWriter.AppendAsync([new("other-1", DateTimeOffset.UtcNow, "other-process", "Information",
            "started", "Another dashboard is still open")], Token);
        var activePath = Assert.Single(Directory.GetFiles(LogDirectory, "*.active.jsonl"));
        try
        {
            await using var log = new FeatureLogService(options);
            await log.StartAsync(Token);
            for (var i = 0; i < 20; i++)
                log.Write("upload", $"event-{i}", new string('x', 400));
            await log.StopAsync(Token);

            Assert.True(File.Exists(activePath));
            var response = await log.ReadAsync(new FeatureLogQuery(), cancellationToken: Token);
            Assert.Contains(response.Entries, e => e.Id == "other-1");
            Assert.Contains(response.Entries, e => e.EventName == "event-19");
            Assert.Equal(3, Directory.GetFiles(LogDirectory, "*.jsonl").Length);
        }
        finally { await otherWriter.CloseAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FieldAndReadLimitsBoundPersistedEvents_AndBlankCategoriesUseDefaults()
    {
        await using var log = new FeatureLogService(Options(maxReadEntries: 3));
        await log.StartAsync(Token);
        for (var i = 0; i < 8; i++)
            log.Write("upload", $"event-{i}", "Older event");
        log.Write("  ", " ", new string('m', 3000), new string('o', 200), new string('s', 800), new string('t', 100));
        await log.StopAsync(Token);

        var page = await log.ReadAsync(new FeatureLogQuery(Limit: int.MaxValue), cancellationToken: Token);
        Assert.Equal(3, page.Entries.Count);
        var latest = page.Entries[0];
        Assert.Equal("general", latest.Feature);
        Assert.Equal("event", latest.EventName);
        Assert.Equal(2048, latest.Message.Length);
        Assert.Equal(128, latest.OperationId!.Length);
        Assert.Equal(512, latest.Subject!.Length);
        Assert.Equal(64, latest.Status!.Length);
        Assert.Empty((await log.ReadAsync(new FeatureLogQuery(Offset: int.MaxValue), cancellationToken: Token)).Entries);
        Assert.Single((await log.ReadAsync(new FeatureLogQuery(Offset: -1, Limit: 0), cancellationToken: Token)).Entries);
    }

    private FeatureLogOptions Options(int queueCapacity = 1024, int segmentBytes = 2 * 1024 * 1024,
        int retainedFiles = 8, int maxReadEntries = 10_000) => new()
    {
        DirectoryPath = LogDirectory,
        QueueCapacity = queueCapacity,
        MaxSegmentBytes = segmentBytes,
        MaxRetainedFiles = retainedFiles,
        MaxReadEntries = maxReadEntries,
        ReadCacheDuration = TimeSpan.Zero
    };
}
