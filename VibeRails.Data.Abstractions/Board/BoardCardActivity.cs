namespace VibeRails.Services.Board;

/// <summary>Only live session references for a selected card; no card text or historical sessions.</summary>
public sealed record BoardCardActivityRecord(string CardId, string? SessionId, string? Origin);

public partial interface IBoardStore
{
    Task<IReadOnlyList<BoardCardActivityRecord>> GetCardActivityAsync(string projectPath, string boardId,
        IReadOnlyList<string> cardIds, IReadOnlyList<string> liveSessionIds, CancellationToken cancellationToken = default);
}
