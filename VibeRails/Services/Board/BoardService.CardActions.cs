using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardService
{
    /// <summary>Human-only comment deletion; no matching MCP capability.</summary>
    public Task<bool> DeleteCommentAsync(string projectPath, string idOrKey, string commentId,
        BoardAuthor author, CancellationToken cancellationToken = default) =>
        store.DeleteCommentAsync(projectPath, idOrKey, commentId, author, cancellationToken);

    /// <summary>Combines two project cards in the store's transaction.</summary>
    public async Task<BoardCardResponse?> MergeCardsAsync(string projectPath, string sourceId,
        string targetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new BoardValidationException("Choose a card to merge into.");
        var merged = await store.MergeCardsAsync(projectPath, sourceId, targetId, cancellationToken);
        return merged is null ? null : await GetCardAsync(projectPath, merged.Id, cancellationToken);
    }
}
