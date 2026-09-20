using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public partial interface IBoardStore
{
    // Boards. A project always has at least one; every column belongs to exactly one. Methods
    // that take an optional boardId (after the token, house style) treat null as the project's
    // default board — the first by position — so single-board callers and tests read unchanged.
    Task<IReadOnlyList<BoardRecord>> GetBoardsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<BoardRecord?> GetBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    /// <summary>Creates a board with the default lanes. Position is appended.</summary>
    Task<BoardRecord> CreateBoardAsync(string projectPath, string name, CancellationToken cancellationToken = default);
    Task<BoardRecord?> RenameBoardAsync(string projectPath, string boardId, string name, CancellationToken cancellationToken = default);
    /// <summary>Deletes the board with its lanes and cards. Refuses the project's last board.</summary>
    Task<BoardDeleteResult?> DeleteBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Seeds the default board and its lanes when the project has none. True when it did.</summary>
    Task<bool> EnsureDefaultColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    /// <summary>Every lane of every board in the project, ordered by board then lane position.</summary>
    Task<IReadOnlyList<BoardColumnRecord>> GetAllColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Card count per board id; boards without cards are absent.</summary>
    Task<IReadOnlyDictionary<string, int>> CountCardsByBoardAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord?> GetColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord> CreateColumnAsync(string projectPath, string name, string color, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardColumnRecord?> UpdateColumnAsync(string projectPath, string columnId, string? name, string? color, CancellationToken cancellationToken = default);
    Task<BoardColumnDeleteResult?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default, string? boardId = null);

    Task<IReadOnlyList<BoardCardRecord>> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardDetailRecord?> GetCardDetailAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default);
    Task<BoardCardRecord?> UpdateCardAsync(string projectPath, string cardId, BoardCardPatch patch, CancellationToken cancellationToken = default);
    Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, CancellationToken cancellationToken = default);

    Task<BoardCommentRecord?> AddCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    /// <summary>Appends an agent-scratchpad entry (<see cref="BoardCommentKinds.Note"/>); never part of the comment stream.</summary>
    Task<BoardCommentRecord?> AddNoteAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardCommentRecord>> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    /// <summary>Ended time, exit code and chat summary for a session, when the Sessions / ChatSummary tables exist in this host; null otherwise.</summary>
    Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<BoardSessionRecord?> LinkSessionAsync(string projectPath, string cardId, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default);
    Task<BoardSessionRecord?> RenameSessionAsync(string projectPath, string cardId, string sessionId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> UnlinkSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    Task<BoardSessionLink?> FindSessionLinkAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<BoardAuthor?> FindSessionAuthorAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardSessionRecord>> GetSessionsForProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<bool> DeleteAttachmentAsync(string projectPath, string cardId, string attachmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BoardCommitRecord>> GetCommitsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    Task<BoardCommitRecord?> AddCommitAsync(string projectPath, string cardId, string sha, string author, string message, DateTime committedUtc, SandboxDiffResponse snapshot, CancellationToken cancellationToken = default);
    Task<SandboxDiffResponse?> GetCommitSnapshotAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
    Task<bool> RemoveCommitAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
}
