using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Services.BertV2;

namespace VibeRails.Jobs;

/// <summary>
/// Embeds cleaned user inputs into the BERT vector store. Picks up
/// Runs at low priority every 5 minutes.
/// </summary>
public sealed class BertEmbeddingBackfillJob(
    ILogger<BertEmbeddingBackfillJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private const int BatchSize = 200;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override JobPriority Priority => JobPriority.High;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
        var bertInputService = scope.ServiceProvider.GetRequiredService<IBertV2InputService>();

        var batch = await repository.GetUnembeddedCleanedInputsAsync(BatchSize, cancellationToken);
        if (batch.Count == 0)
        {
            Serilog.Log.Debug("[BertEmbeddingBackfillJob] No rows to embed.");
            return;
        }

        Serilog.Log.Information(
            "[BertEmbeddingBackfillJob] Batch begin. count={Count} firstSessionId={FirstSessionId} lastSessionId={LastSessionId}",
            batch.Count, batch[0].SessionId, batch[^1].SessionId);

        var ok = 0;
        var failed = 0;
        var batchSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var row in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                bertInputService.Capture(row.SessionId, row.UserInputId, row.CleanedText ?? string.Empty);
                await repository.MarkCleanedInputEmbeddedAsync(row.CleanedId, cancellationToken);
                ok++;
                // Per-row log is at Debug to keep volume down; elevate to Information on failure below.
                Serilog.Log.Debug(
                    "[BertEmbeddingBackfillJob] Row embedded. sessionId={SessionId} cleanedId={CleanedId} userInputId={UserInputId} chars={Chars} durationMs={DurationMs}",
                    row.SessionId, row.CleanedId, row.UserInputId, row.CleanedText?.Length ?? 0, rowSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                failed++;
                Serilog.Log.Warning(
                    ex,
                    "[BertEmbeddingBackfillJob] Row failed. sessionId={SessionId} cleanedId={CleanedId} userInputId={UserInputId} chars={Chars} durationMs={DurationMs}",
                    row.SessionId, row.CleanedId, row.UserInputId, row.CleanedText?.Length ?? 0, rowSw.ElapsedMilliseconds);
            }
        }

        Serilog.Log.Information(
            "[BertEmbeddingBackfillJob] Batch end. count={Count} ok={Ok} failed={Failed} batchDurationMs={BatchDurationMs}",
            batch.Count, ok, failed, batchSw.ElapsedMilliseconds);
    }
}
