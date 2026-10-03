namespace VibeRails.Services.Board;

/// <summary>Only live session references for a selected card; no card text or historical sessions.</summary>
public sealed record BoardCardActivityRecord(string CardId, string? SessionId, string? Origin);

/// <summary>A running local Automation, shared by all root windows. ColumnId is its entry lane, if still on the card's board.</summary>
public sealed record BoardRunningAutomation(string CardId, string BoardId, string? ColumnId, string? RunId = null);

public partial interface IBoardStore
{
    /// <summary>Running Board runs from local Jobs, scoped to existing project cards, without loading card text or history.</summary>
    Task<IReadOnlyList<BoardRunningAutomation>> GetRunningAutomationsAsync(string projectPath,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardCardActivityRecord>> GetCardActivityAsync(string projectPath, string boardId,
        IReadOnlyList<string> cardIds, IReadOnlyList<string> liveSessionIds, CancellationToken cancellationToken = default);
}
