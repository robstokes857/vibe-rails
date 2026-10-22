namespace VibeRails.Services.Board.PostProcessing;

/// <summary>Counts from one post-processing pass, for the job log and tests.</summary>
public readonly record struct BoardPostProcessingPass(int Candidates, int Processed, int Ghosts, int Deferred, int Skipped);

/// <summary>
/// Runs every <see cref="IBoardCardPostProcessor"/> once on each card that has settled in its
/// board's last lane (VIBE-185), across all local projects, then marks it processed together with
/// the outcomes. Settled means no card activity for <see cref="SettleDelay"/>, so a commit or GitHub
/// link added just after the move still counts. Cards that existed before post-processing are
/// never candidates (the store's <c>PostProcessed</c> default), so nothing is backfilled.
/// </summary>
public sealed class BoardCardPostProcessingService(IBoardStore store, IEnumerable<IBoardCardPostProcessor> processors)
{
    /// <summary>How long a card must sit untouched in the last lane before its pass.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(10);

    /// <summary>How long a deferring processor may hold a card back before it is processed without that outcome.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>Cards read per pass; deferred ones are read again on the next pass.</summary>
    public const int BatchSize = 100;

    private readonly IReadOnlyList<IBoardCardPostProcessor> _processors = processors.ToList();

    public async Task<BoardPostProcessingPass> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var candidates = await store.GetPostProcessingCandidatesAsync(nowUtc - SettleDelay, BatchSize, cancellationToken);
        int processed = 0, ghosts = 0, deferred = 0, skipped = 0;
        foreach (var card in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? ghost = null;
            var defer = false;
            foreach (var processor in _processors)
            {
                BoardCardPostProcessorOutcome outcome;
                try
                {
                    outcome = await processor.ProcessAsync(card, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Serilog.Log.Warning(ex, "[BoardPostProcessing] {Processor} failed on {Card}; deferring", processor.Name, card.Key);
                    outcome = BoardCardPostProcessorOutcome.Later(ex.Message);
                }
                if (outcome.Defer)
                {
                    defer = true;
                    Serilog.Log.Information("[BoardPostProcessing] {Processor} deferred {Card}: {Note}", processor.Name, card.Key, outcome.Note);
                    continue;
                }
                ghost ??= outcome.GhostReason;
            }

            if (defer && nowUtc - card.UpdatedUtc < GiveUpAfter)
            {
                deferred++;
                continue;
            }
            if (!await store.CompletePostProcessingAsync(card, new BoardPostProcessingResult(ghost), cancellationToken))
            {
                skipped++;
                continue;
            }
            processed++;
            if (ghost is not null)
            {
                ghosts++;
                Serilog.Log.Information("[BoardPostProcessing] Ghost on {Card} ({Reason}) in {Project}", card.Key, ghost, card.ProjectPath);
            }
        }
        return new BoardPostProcessingPass(candidates.Count, processed, ghosts, deferred, skipped);
    }
}
