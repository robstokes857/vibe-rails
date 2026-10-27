namespace VibeRails.Services.Board.PostProcessing;

/// <summary>Event-driven, single-card checks. Never registers a timer or requeues post-processing.</summary>
public sealed class BoardGhostService(IBoardStore store, GhostCardPostProcessor processor)
{
    /// <summary>Checks a single saved card and conditionally updates its local annotation.</summary>
    public async Task<BoardGhostCheckResponse?> RecheckAsync(string project, string cardId, CancellationToken ct)
    {
        var check = await store.GetGhostCheckAsync(project, cardId, ct);
        if (check is null) return null;
        if (!check.IsFinished)
            return new("not-applicable", "The completion check applies after this card reaches the board's final lane.", check.GhostReason);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        BoardCardPostProcessorOutcome outcome;
        try { outcome = await processor.ProcessAsync(check.Card, timeout.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Serilog.Log.Warning(ex, "[BoardGhost] Could not check {Card}", check.Card.Key);
            return new("unverified", "Code landing could not be verified. The existing ghost marker was kept; try again later.", check.GhostReason);
        }
        if (outcome.Defer || outcome.Note is not null)
            return new("unverified", "Code landing could not be verified. The existing ghost marker was kept; try again later.", check.GhostReason);
        if (!await store.ApplyGhostCheckAsync(check, outcome.GhostReason, ct))
        {
            // Report the marker the kept, newer state carries, not this discarded result.
            var current = await store.GetGhostCheckAsync(project, cardId, ct);
            return new("stale", "The card changed during the check. Its newer state was kept; check again.", current?.GhostReason);
        }
        return outcome.GhostReason switch
        {
            BoardGhostReasons.NoCode => new("no-code", "Ghost: an agent worked on this completed card, but no commit or PR is linked.", outcome.GhostReason),
            BoardGhostReasons.NotLanded => new("not-landed", "Ghost: linked code has not landed on the project's default branch. An unpushed local default-branch commit counts as landed.", outcome.GhostReason),
            _ => new("clear", "No ghost: code has landed or this card is exempt from the completion check.", null)
        };
    }

    /// <summary>A committed link remains successful even when its follow-up check is unavailable.</summary>
    public async Task RecheckAfterLinkAsync(string project, string cardId, CancellationToken ct)
    {
        try
        {
            var check = await store.GetGhostCheckAsync(project, cardId, ct);
            if (check?.GhostReason is not null) await RecheckAsync(project, cardId, ct);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[BoardGhost] Follow-up check unavailable for {Card}", cardId); }
    }
}
