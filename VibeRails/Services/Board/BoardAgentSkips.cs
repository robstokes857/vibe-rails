using VibeRails.Launches;

namespace VibeRails.Services.Board;

/// <summary>
/// An agent skips one lane step with a stated reason (VIBE-338, owner decision 2026-11-16): the lane
/// carries on straight away, and the skip stays on the card's tile and rail as "Skipped by agent"
/// until a person dismisses it. The skip, its report row, the agent's comment and that marker commit
/// together behind <see cref="IBoardStore.SkipLaunchEventForAgentAsync"/>.
/// </summary>
public static class BoardAgentSkips
{
    public const int MinReasonLength = 20;
    public const int MaxReasonLength = 2000;

    /// <summary>A trimmed reason, or a validation error naming the bounds.</summary>
    public static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? "";
        if (trimmed.Length < MinReasonLength || trimmed.Length > MaxReasonLength)
            throw new BoardValidationException(
                $"Give a reason of {MinReasonLength}–{MaxReasonLength:N0} characters: why this step does not apply to this card. The owner reads it.");
        return trimmed;
    }

    /// <summary>
    /// Skips the card's current lane entry <paramref name="eventKey"/>. A step whose run has started
    /// cannot be skipped by an agent: stopping another agent's live run (for example the reviewer's)
    /// stays with the user's Stop and skip.
    /// </summary>
    public static async Task<BoardAgentSkipRecord> SkipAsync(IBoardStore store, ILaunchStore? launches, string project,
        string cardId, string? eventKey, string? reason, BoardAuthor author, CancellationToken ct)
    {
        var text = ValidateReason(reason);
        if (string.IsNullOrWhiteSpace(eventKey))
            throw new BoardValidationException("Pass the step's eventKey: get_board_agent_status lists each current lane entry with its key.");
        var entry = (await store.GetLaunchEventsAsync(project, cardId, ct)).FirstOrDefault(e => e.Id == eventKey.Trim() && e.Current)
            ?? throw new BoardValidationException("No current lane step on this card has that eventKey. get_board_agent_status lists the current ones.");
        if (entry.SkipRequested || entry.Verdict is "passed" or "skipped")
            throw new BoardConflictException($"This step is already {(entry.Verdict == "passed" ? "passed" : "skipped")}.");
        var run = entry.RunId is null || launches is null ? null : await launches.GetRunAsync(entry.RunId, ct);
        var status = run?.Status ?? entry.ProcessStatus;
        if (entry.RunId is not null && status is "claimed" or "running" or "uncertain")
            throw new BoardConflictException("This step is running. An agent cannot stop a running step: report through it, or ask the user to use Stop and skip.");
        var name = launches is null ? null : entry.DefinitionType == "preset"
            ? (await launches.GetPresetAsync(entry.DefinitionId, ct))?.Name
            : (await launches.GetActionAsync(entry.DefinitionId, ct))?.Name;
        var step = BoardPromptComposer.SanitizeLine(string.IsNullOrWhiteSpace(name) ? entry.DefinitionId : name, 120);
        return await store.SkipLaunchEventForAgentAsync(project, cardId, entry.Id, step, text, author, ct)
            ?? throw new BoardConflictException("The step changed before it could be skipped. Read get_board_agent_status and try again.");
    }
}
