using System.Diagnostics;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

public sealed class BertEmbeddingBackfillJob(
    ILogger<BertEmbeddingBackfillJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private const int BatchSize = 25;

    // Transient SQLite errors should not eat into a row's failure quota — those
    // are environmental and disappear on retry, so counting them risks burning
    // through BertEmbedFailureSkipThreshold on a healthy row that just happened
    // to overlap with a writer holding the file lock.
    //   5 = SQLITE_BUSY, 6 = SQLITE_LOCKED.
    private static bool IsTransientSqliteError(SqliteException ex)
        => ex.SqliteErrorCode is 5 or 6;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(3);
    protected override JobPriority Priority => JobPriority.Med;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

        var batch = await repository.GetUnembeddedUserInputsAsync(BatchSize, cancellationToken);
        if (batch.Count == 0)
        {
            Serilog.Log.Debug("[Job:BertEmbeddingBackfillJob] No rows to embed.");
            return;
        }

        IBertV2InputService bertInputService;
        try
        {
            bertInputService = scope.ServiceProvider.GetRequiredService<IBertV2InputService>();
        }
        catch (Exception ex)
        {
            // JobBase logs uncaught exceptions every tick at Error severity, so a broken
            // deploy (missing native onnxruntime.dll, ORT native/managed version mismatch,
            // etc.) stays loud until fixed instead of silently disabling the job.
            throw new InvalidOperationException(
                $"[BERT-BROKEN] BERT embedder failed to initialize. Pending rows will not be embedded. " +
                $"This usually means the native ONNX Runtime library is missing from the install directory " +
                $"or mismatched with Microsoft.ML.OnnxRuntime. Check that onnxruntime.{{dll,so,dylib}} sits " +
                $"alongside vb at '{AppContext.BaseDirectory}'. batchSize={batch.Count} pid={Environment.ProcessId}.",
                ex);
        }

        Serilog.Log.Information(
            "[Job:BertEmbeddingBackfillJob] Batch begin. count={Count} firstId={FirstId} lastId={LastId}",
            batch.Count, batch[0].Id, batch[^1].Id);

        var embeddedIds = new List<long>(batch.Count);
        var failedIds = new List<long>();
        var batchSw = Stopwatch.StartNew();
        try
        {
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowSw = Stopwatch.StartNew();
                try
                {
                    bertInputService.Capture(row.SessionId, row.Id, row.InputText);
                    embeddedIds.Add(row.Id);
                    Serilog.Log.Debug(
                        "[Job:BertEmbeddingBackfillJob] Row embedded. sessionId={SessionId} userInputId={UserInputId} chars={Chars} durationMs={DurationMs}",
                        row.SessionId, row.Id, row.InputText.Length, rowSw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                    // Don't bump the failure count — this is contention, not corruption.
                    // The row stays unembedded and the next tick picks it up.
                    Serilog.Log.Warning(
                        ex,
                        "[Job:BertEmbeddingBackfillJob] Row deferred due to transient SQLite contention. sessionId={SessionId} userInputId={UserInputId} durationMs={DurationMs}",
                        row.SessionId, row.Id, rowSw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    failedIds.Add(row.Id);
                    Serilog.Log.Warning(
                        ex,
                        "[Job:BertEmbeddingBackfillJob] Row failed. sessionId={SessionId} userInputId={UserInputId} chars={Chars} durationMs={DurationMs}",
                        row.SessionId, row.Id, row.InputText.Length, rowSw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            // Persist marks even on cancellation: Capture's vector-store writes are
            // already on disk, so without a corresponding mark the next tick would
            // re-embed the same rows. Use CancellationToken.None so shutdown still
            // commits a fast UPDATE — vector store is keyed (sessionId, userInputId)
            // so re-embedding is correctness-safe but wastes ONNX compute.
            if (embeddedIds.Count > 0)
            {
                await repository.MarkUserInputsBertEmbeddedAsync(embeddedIds, DateTime.UtcNow, CancellationToken.None);
            }
            // Bump the failure count for poison-pill rows so they roll off the queue
            // after BertEmbedFailureSkipThreshold attempts instead of blocking every
            // newer row behind them forever.
            if (failedIds.Count > 0)
            {
                await repository.IncrementUserInputBertEmbedFailureCountsAsync(failedIds, CancellationToken.None);
            }
        }

        Serilog.Log.Information(
            "[Job:BertEmbeddingBackfillJob] Batch end. count={Count} ok={Ok} failed={Failed} batchDurationMs={BatchDurationMs}",
            batch.Count, embeddedIds.Count, failedIds.Count, batchSw.ElapsedMilliseconds);
    }
}
