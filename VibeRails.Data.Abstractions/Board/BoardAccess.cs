namespace VibeRails.Services.Board;

/// <summary>Shared imported boards are retained for reading, never for local work or edits.</summary>
public static class BoardAccess
{
    /// <summary>Guidance shared by store writes, launch services and sync controls.</summary>
    public const string ReadOnlyMessage =
        "This board was shared with you and is read-only. It no longer syncs; view the current board on viberails.ai. Local data is kept.";

    /// <summary>Rejects an operation that would edit or execute work on another account's board.</summary>
    public static async Task RequireWritableAsync(IBoardStore store, string projectPath, string boardId,
        CancellationToken cancellationToken = default)
    {
        if ((await store.GetSharedBoardOriginsAsync(projectPath, cancellationToken)).ContainsKey(boardId))
            throw new BoardValidationException(ReadOnlyMessage);
    }
}
