using VibeRails.Utils;
using VibeRails.Services.Board;

namespace VibeRails.Services.Terminal;

/// <summary>Shared launch context, gated by the built-in or saved-environment nudge.</summary>
internal static class EveryContextPrompt
{
    internal static string Prefix(string? message) => string.IsNullOrWhiteSpace(message)
        ? "" : "VibeRails Every Context:\n" + message.Trim() + "\n\n";

    internal static async Task<string?> ResolveMessageAsync(IBoardStore store, string projectPath, string? boardId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        if (string.IsNullOrWhiteSpace(boardId))
            boardId = (await store.GetLocalBoardsAsync(cancellationToken))
                .Where(board => ProjectPathComparer.Matches(board.ProjectPath, projectPath))
                .OrderBy(board => board.Position).FirstOrDefault()?.Id;
        return boardId is null ? null : (await store.GetEveryContextSettingsAsync(projectPath, boardId, cancellationToken))?.Message;
    }
}
