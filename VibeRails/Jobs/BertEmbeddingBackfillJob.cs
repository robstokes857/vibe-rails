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
            return;

        _logger.LogInformation(
            "[BertEmbeddingBackfillJob] Embedding {Count} cleaned input(s)",
            batch.Count);

        foreach (var row in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bertInputService.Capture(row.SessionId, row.UserInputId, row.CleanedText);
                await repository.MarkCleanedInputEmbeddedAsync(row.CleanedId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[BertEmbeddingBackfillJob] Failed to embed CleanedUserInput {CleanedId}",
                    row.CleanedId);
            }
        }
    }
}
