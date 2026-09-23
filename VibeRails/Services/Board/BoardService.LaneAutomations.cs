using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>
/// One lane Automation as an agent should read it (VB-34): its name, a one-line summary built
/// from its own definition (Worker, prompt, scripts), where its output lands, and whether the
/// scheduler would run it for an entry recorded now. <see cref="Unavailable"/> names the gate
/// that would consume the entry without a run; <see cref="ActiveRunId"/> is the self-overlap
/// guard's reason. Both are evaluated at read time; the scheduler re-checks them when the entry
/// settles.
/// </summary>
public sealed record BoardLaneAutomationInfo(
    long JobId,
    string Name,
    string Summary,
    string Output,
    string? Unavailable,
    string? ActiveRunId,
    bool ActiveRunIsRunning)
{
    public bool WouldQueue => Unavailable is null && ActiveRunId is null;
}

/// <summary>A card's lane entry that the scheduler has not consumed yet.</summary>
public sealed record BoardPendingLaneAutomationInfo(BoardLaneAutomationInfo Automation, string ColumnId, DateTime DueUtc);

/// <summary>
/// A move with the caller's choices. <see cref="SkipLaneAutomations"/> is per call and never
/// sticky; the skip is recorded as a comment by <see cref="Author"/> when one is supplied.
/// </summary>
public sealed record BoardCardMoveRequest(string ColumnIdOrName, int? Position = null, bool SkipLaneAutomations = false, BoardAuthor? Author = null);

/// <summary>
/// What a lane entry does (or did) beyond moving the card. <see cref="EnteredLane"/> is false for
/// a same-lane reposition, which records no entry. <see cref="Automations"/> are the destination
/// lane's Automations in their configured order; <see cref="Cancelled"/> are earlier entries of
/// this card that the move replaced before they settled.
/// </summary>
public sealed record BoardLaneEntryReport(
    string LaneName,
    bool EnteredLane,
    bool SkippedByCaller,
    IReadOnlyList<BoardLaneAutomationInfo> Automations,
    IReadOnlyList<BoardLaneAutomationInfo> Cancelled,
    DateTime AtUtc);

public sealed record BoardCardMoveResult(BoardCardResponse Card, BoardLaneEntryReport LaneEntry);

public partial interface IBoardService
{
    /// <summary>Moves a card and reports what the destination lane triggers. Same validation as the plain move.</summary>
    Task<BoardCardMoveResult?> MoveCardAsync(string projectPath, string idOrKey, BoardCardMoveRequest move, CancellationToken cancellationToken = default);
    /// <summary>The lane-entry report a move would produce, without moving.</summary>
    Task<BoardLaneEntryReport?> PreviewMoveAsync(string projectPath, string idOrKey, string columnIdOrName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardLaneAutomationInfo>> GetLaneAutomationsAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    /// <summary>Lane Automations for several lanes with one definition read; every requested lane is a key.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<BoardLaneAutomationInfo>>> GetLaneAutomationsByLaneAsync(string projectPath, IReadOnlyList<string> columnIds, CancellationToken cancellationToken = default);
    /// <summary>Null when the card does not exist; empty when nothing is pending.</summary>
    Task<IReadOnlyList<BoardPendingLaneAutomationInfo>?> GetPendingLaneAutomationsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    /// <summary>The settling delay the store's lane-entry triggers apply (DueUnixMs = now + 60 s).</summary>
    public const int LaneAutomationSettleSeconds = 60;

    private const int AutomationNameChars = 80;
    private const int WorkerPromptChars = 120;

    public async Task<BoardCardMoveResult?> MoveCardAsync(string projectPath, string idOrKey, BoardCardMoveRequest move, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        // A lane name means a lane on the card's own board; an id can move it to another board.
        var column = await FindColumnAsync(projectPath, move.ColumnIdOrName, cancellationToken, NormalizeBoardId(existing.BoardId))
            ?? throw new BoardValidationException($"Lane not found: {move.ColumnIdOrName}");
        if (move.Position is < 0)
            throw new BoardValidationException("Position cannot be negative.");
        var entering = !string.Equals(column.Id, existing.ColumnId, StringComparison.Ordinal);
        // Read before the move: the lane-entry trigger replaces this card's pending entries.
        var pendingBefore = entering
            ? await store.GetPendingLaneAutomationsAsync(projectPath, existing.Id, cancellationToken)
            : [];
        var moved = await store.MoveCardAsync(projectPath, existing.Id, column.Id, move.Position, move.SkipLaneAutomations, cancellationToken);
        if (moved is null)
            return null;
        var automations = entering
            ? await DescribeLaneAutomationsAsync(store, projectPath, column.Id, cancellationToken)
            : [];
        var cancelled = await DescribeCancelledAsync(projectPath, pendingBefore, entering && !move.SkipLaneAutomations ? automations : [], cancellationToken);
        if (entering && move.SkipLaneAutomations && automations.Count > 0 && move.Author is not null)
        {
            // The skip leaves no pending row behind, so the card itself must say why no run
            // followed this entry. Best effort after the move commits, like other rail writes.
            await store.AddCommentAsync(projectPath, existing.Id, move.Author, SkipComment(column.Name, automations), cancellationToken);
        }
        var card = await GetCardAsync(projectPath, moved.Id, cancellationToken);
        return card is null
            ? null
            : new BoardCardMoveResult(card, new BoardLaneEntryReport(column.Name, entering, move.SkipLaneAutomations, automations, cancelled, moved.UpdatedUtc));
    }

    public async Task<BoardLaneEntryReport?> PreviewMoveAsync(string projectPath, string idOrKey, string columnIdOrName, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var column = await FindColumnAsync(projectPath, columnIdOrName, cancellationToken, NormalizeBoardId(existing.BoardId))
            ?? throw new BoardValidationException($"Lane not found: {columnIdOrName}");
        var entering = !string.Equals(column.Id, existing.ColumnId, StringComparison.Ordinal);
        var automations = entering
            ? await DescribeLaneAutomationsAsync(store, projectPath, column.Id, cancellationToken)
            : [];
        var pending = entering
            ? await store.GetPendingLaneAutomationsAsync(projectPath, existing.Id, cancellationToken)
            : [];
        var cancelled = await DescribeCancelledAsync(projectPath, pending, automations, cancellationToken);
        return new BoardLaneEntryReport(column.Name, entering, false, automations, cancelled, DateTime.UtcNow);
    }

    public Task<IReadOnlyList<BoardLaneAutomationInfo>> GetLaneAutomationsAsync(string projectPath, string columnId, CancellationToken cancellationToken = default) =>
        DescribeLaneAutomationsAsync(store, projectPath, columnId, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<BoardLaneAutomationInfo>>> GetLaneAutomationsByLaneAsync(string projectPath, IReadOnlyList<string> columnIds, CancellationToken cancellationToken = default) =>
        DescribeLaneAutomationsByLaneAsync(store, projectPath, columnIds, cancellationToken);

    public async Task<IReadOnlyList<BoardPendingLaneAutomationInfo>?> GetPendingLaneAutomationsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var pending = await store.GetPendingLaneAutomationsAsync(projectPath, existing.Id, cancellationToken);
        if (pending.Count == 0)
            return [];
        var described = (await store.DescribeLaneAutomationsAsync(projectPath, pending.Select(p => p.JobId).Distinct().ToList(), cancellationToken))
            .ToDictionary(d => d.JobId, Describe);
        return pending.Select(p => new BoardPendingLaneAutomationInfo(described[p.JobId], p.ColumnId, p.DueUtc)).ToList();
    }

    /// <summary>Shared with the launch prompt, which has the store but not the service.</summary>
    internal static async Task<IReadOnlyList<BoardLaneAutomationInfo>> DescribeLaneAutomationsAsync(IBoardStore store, string projectPath, string columnId, CancellationToken cancellationToken)
    {
        var selection = await store.GetLaneAutomationAsync(projectPath, columnId, cancellationToken);
        if (selection is null || selection.JobIds.Count == 0)
            return [];
        return (await store.DescribeLaneAutomationsAsync(projectPath, selection.JobIds, cancellationToken)).Select(Describe).ToList();
    }

    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<BoardLaneAutomationInfo>>> DescribeLaneAutomationsByLaneAsync(
        IBoardStore store, string projectPath, IReadOnlyList<string> columnIds, CancellationToken cancellationToken)
    {
        var selections = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal);
        foreach (var columnId in columnIds.Distinct(StringComparer.Ordinal))
        {
            var selection = await store.GetLaneAutomationAsync(projectPath, columnId, cancellationToken);
            if (selection is { JobIds.Count: > 0 })
                selections[columnId] = selection.JobIds;
        }
        var described = selections.Count == 0
            ? new Dictionary<long, BoardLaneAutomationInfo>()
            : (await store.DescribeLaneAutomationsAsync(projectPath, selections.Values.SelectMany(ids => ids).Distinct().ToList(), cancellationToken))
                .ToDictionary(d => d.JobId, Describe);
        var result = new Dictionary<string, IReadOnlyList<BoardLaneAutomationInfo>>(StringComparer.Ordinal);
        foreach (var columnId in columnIds)
            result[columnId] = selections.TryGetValue(columnId, out var ids)
                ? ids.Select(id => described[id]).ToList()
                : [];
        return result;
    }

    private async Task<IReadOnlyList<BoardLaneAutomationInfo>> DescribeCancelledAsync(
        string projectPath, IReadOnlyList<BoardPendingLaneAutomation> pending, IReadOnlyList<BoardLaneAutomationInfo> requeued, CancellationToken cancellationToken)
    {
        // An Automation the destination lane records again is replaced, not cancelled. Pending
        // rows written by two triggers can straddle a millisecond, so order by id, not due time.
        var ids = pending.Select(p => p.JobId).Distinct().Where(id => requeued.All(a => a.JobId != id)).Order().ToList();
        if (ids.Count == 0)
            return [];
        return (await store.DescribeLaneAutomationsAsync(projectPath, ids, cancellationToken)).Select(Describe).ToList();
    }

    /// <summary>
    /// Wording comes from the definition, not from the Automation's kind: a review Worker, a
    /// PR-opening Worker and a script-only workflow all read the same way.
    /// </summary>
    internal static BoardLaneAutomationInfo Describe(BoardLaneAutomationDefinition definition)
    {
        var name = definition.Name is null
            ? $"Automation #{definition.JobId}"
            : OneLine(definition.Name, AutomationNameChars);
        var unavailable = definition.Name is null ? "no longer exists"
            : definition.Deleted ? "was deleted"
            : !definition.Enabled ? "is disabled"
            : !definition.InProject ? "belongs to another repository"
            : !definition.HasActions ? "has no actions"
            : null;

        var parts = new List<string>(2);
        if (definition.WorkerName is not null)
        {
            var worker = new StringBuilder("Worker \"").Append(OneLine(definition.WorkerName, AutomationNameChars)).Append('"');
            if (definition.WorkerCli.Length > 0) worker.Append(" (").Append(definition.WorkerCli).Append(')');
            if (definition.WorkerPrompt.Length > 0) worker.Append(": \"").Append(OneLine(definition.WorkerPrompt, WorkerPromptChars)).Append('"');
            parts.Add(worker.ToString());
        }
        if (definition.ScriptPaths.Count > 0)
        {
            var names = definition.ScriptPaths.Take(3).Select(path => OneLine(Path.GetFileName(path), AutomationNameChars));
            parts.Add($"{definition.ScriptPaths.Count} script{(definition.ScriptPaths.Count == 1 ? "" : "s")} ({string.Join(", ", names)}{(definition.ScriptPaths.Count > 3 ? ", …" : "")})");
        }
        var summary = parts.Count > 0 ? string.Join(" + ", parts)
            : definition.Name is null ? "definition unavailable"
            : "no actions";
        var output = definition.WorkerName is not null ? "a Worker terminal run linked to the card's Sessions rail"
            : definition.ScriptPaths.Count > 0 ? "script output recorded on the card's Sessions rail"
            : "nothing";
        return new BoardLaneAutomationInfo(definition.JobId, name, summary, output, unavailable, definition.ActiveRunId, definition.ActiveRunIsRunning);
    }

    internal static string SkipComment(string laneName, IReadOnlyList<BoardLaneAutomationInfo> automations) =>
        $"Moved to {laneName}; lane Automations skipped at the caller's request: {QuotedNames(automations)}.";

    internal static string QuotedNames(IEnumerable<BoardLaneAutomationInfo> automations) =>
        string.Join(", ", automations.Select(a => $"\"{a.Name}\""));

    /// <summary>First line only, control characters removed, bounded. Definitions are user data too.</summary>
    internal static string OneLine(string? value, int maxChars)
    {
        var text = value ?? string.Empty;
        var end = text.IndexOfAny(['\r', '\n']);
        if (end >= 0) text = text[..end];
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            builder.Append(char.IsControl(character) ? ' ' : character);
        text = builder.ToString().Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= maxChars ? text : text[..(maxChars - 1)].TrimEnd() + "…";
    }
}
