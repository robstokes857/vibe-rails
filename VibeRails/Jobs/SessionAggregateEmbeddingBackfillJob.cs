using System.Diagnostics;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

public sealed class SessionAggregateEmbeddingBackfillJob(
    ILogger<SessionAggregateEmbeddingBackfillJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private const int BatchSize = 5;

    // SQLITE_BUSY / SQLITE_LOCKED are environmental and will pass on retry.
    // Don't burn through BertEmbedFailureSkipThreshold counting them.
    private static bool IsTransientSqliteError(SqliteException ex)
        => ex.SqliteErrorCode is 5 or 6;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override JobPriority Priority => JobPriority.Med;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

        var sessionIds = await repository.GetUnaggregatedEndedSessionIdsAsync(BatchSize, cancellationToken);
        if (sessionIds.Count == 0)
        {
            Serilog.Log.Debug("[Job:SessionAggregateEmbeddingBackfillJob] No ended sessions to embed.");
            return;
        }

        IBertV2SessionEmbeddingService sessionEmbeddingService;
        try
        {
            sessionEmbeddingService = scope.ServiceProvider.GetRequiredService<IBertV2SessionEmbeddingService>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[BERT-BROKEN] Session embedder failed to initialize. Pending sessions will not be embedded. " +
                $"This usually means the native ONNX Runtime library is missing from the install directory " +
                $"or mismatched with Microsoft.ML.OnnxRuntime. Check that onnxruntime.{{dll,so,dylib}} sits " +
                $"alongside vb at '{AppContext.BaseDirectory}'. batchSize={sessionIds.Count} pid={Environment.ProcessId}.",
                ex);
        }

        Serilog.Log.Information(
            "[Job:SessionAggregateEmbeddingBackfillJob] Batch begin. count={Count}",
            sessionIds.Count);

        var embeddedSessionIds = new List<string>(sessionIds.Count);
        var failedSessionIds = new List<string>();
        var totalChunks = 0;
        var batchSw = Stopwatch.StartNew();
        try
        {
            foreach (var sessionId in sessionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessionSw = Stopwatch.StartNew();
                try
                {
                    var inputs = await repository.GetUserInputTextsForSessionAsync(sessionId, cancellationToken);
                    var chunks = sessionEmbeddingService.CaptureSession(sessionId, inputs);
                    embeddedSessionIds.Add(sessionId);
                    totalChunks += chunks;
                    Serilog.Log.Debug(
                        "[Job:SessionAggregateEmbeddingBackfillJob] Session embedded. sessionId={SessionId} inputs={Inputs} chunks={Chunks} durationMs={DurationMs}",
                        sessionId, inputs.Count, chunks, sessionSw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                    // Transient contention — leave the session unmarked so the next
                    // tick retries it without consuming a failure attempt.
                    Serilog.Log.Warning(
                        ex,
                        "[Job:SessionAggregateEmbeddingBackfillJob] Session deferred due to transient SQLite contention. sessionId={SessionId} durationMs={DurationMs}",
                        sessionId, sessionSw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    failedSessionIds.Add(sessionId);
                    Serilog.Log.Warning(
                        ex,
                        "[Job:SessionAggregateEmbeddingBackfillJob] Session failed. sessionId={SessionId} durationMs={DurationMs}",
                        sessionId, sessionSw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            // Persist marks even on cancellation: chunk writes are already on disk and
            // re-running would just overwrite the same (sessionId, chunkIndex) keys.
            // CancellationToken.None lets shutdown commit a fast UPDATE.
            if (embeddedSessionIds.Count > 0)
            {
                await repository.MarkSessionsAggregateEmbeddedAsync(embeddedSessionIds, DateTime.UtcNow, CancellationToken.None);
            }
            // Bump failure counts so a session whose embedding consistently blows up
            // (e.g. one corrupt UserInputs row) stops blocking newer sessions after
            // BertEmbedFailureSkipThreshold attempts.
            if (failedSessionIds.Count > 0)
            {
                await repository.IncrementSessionAggregateEmbedFailureCountsAsync(failedSessionIds, CancellationToken.None);
            }
        }

        Serilog.Log.Information(
            "[Job:SessionAggregateEmbeddingBackfillJob] Batch end. count={Count} ok={Ok} failed={Failed} chunks={Chunks} batchDurationMs={BatchDurationMs}",
            sessionIds.Count, embeddedSessionIds.Count, failedSessionIds.Count, totalChunks, batchSw.ElapsedMilliseconds);
    }
}
