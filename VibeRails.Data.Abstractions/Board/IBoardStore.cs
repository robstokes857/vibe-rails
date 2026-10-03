using VibeRails.DTOs;
namespace VibeRails.Services.Board;
/// <summary>
/// The persistence boundary for all Board state, including lane-entry events. Keep callers on
/// this contract: a future shared, API-backed Board should not require local SQLite access.
/// Session outcomes and Automation definitions remain host-local references, not Board-owned data.
/// </summary>
public partial interface IBoardStore
{
    // Boards. A project always has at least one; every column belongs to exactly one. Methods
    // that take an optional boardId (after the token, house style) treat null as the project's
    // default board — the first by position — so single-board callers and tests read unchanged.
    Task<IReadOnlyList<BoardRecord>> GetBoardsAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Existing local boards across projects, including their stored project identity. Does not seed boards.</summary>
    Task<IReadOnlyList<BoardRecord>> GetLocalBoardsAsync(CancellationToken cancellationToken = default);
    /// <summary>Finds a live local card by exact row ID or full stored permanent key across projects. No short-key or display-ID aliases.</summary>
    Task<BoardCardRecord?> FindLocalCardAsync(string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardRecord?> GetBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    /// <summary>Creates a board with the default lanes. Position is appended.</summary>
    Task<BoardRecord> CreateBoardAsync(string projectPath, string name, CancellationToken cancellationToken = default);
    /// <summary>Creates a board with the default lanes and, when not null, its display prefix, in one transaction.</summary>
    Task<BoardRecord> CreateBoardAsync(string projectPath, string name, string? displayPrefix, CancellationToken cancellationToken = default);
    /// <summary>
    /// Renames the board and sets its display prefix in one transaction. A null name or prefix leaves that
    /// value as it is; an empty prefix clears it, so future cards use the repository default again.
    /// Null when the board is not in the project.
    /// </summary>
    Task<BoardRecord?> RenameBoardAsync(string projectPath, string boardId, string? name, string? displayPrefix, CancellationToken cancellationToken = default);
    /// <summary>Deletes the board with its lanes and cards. Refuses the project's last board.</summary>
    Task<BoardDeleteResult?> DeleteBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Seeds the default board and its lanes when the project has none. True when it did.</summary>
    Task<bool> EnsureDefaultColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    /// <summary>Every lane of every board in the project, ordered by board then lane position.</summary>
    Task<IReadOnlyList<BoardColumnRecord>> GetAllColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Card count per board id; boards without cards are absent.</summary>
    Task<IReadOnlyDictionary<string, int>> CountCardsByBoardAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Live card count per lane id of one board; lanes without cards are absent.</summary>
    Task<IReadOnlyDictionary<string, int>> CountCardsByColumnAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardColumnRecord?> GetColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord> CreateColumnAsync(string projectPath, string name, string color, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardColumnRecord?> UpdateColumnAsync(string projectPath, string columnId, string? name, string? color, CancellationToken cancellationToken = default);
    Task<BoardColumnDeleteResult?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default, string? boardId = null);

    Task<IReadOnlyList<BoardCardRecord>> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardDetailRecord?> GetCardDetailAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    // Card writes append a Card Log entry in the same transaction. A null author means the local user.
    Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    Task<BoardCardRecord?> UpdateCardAsync(string projectPath, string cardId, BoardCardPatch patch, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    /// <summary>Soft-deletes the card: it disappears from every read, but its rows and links stay for a later restore.</summary>
    Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, CancellationToken cancellationToken = default, BoardAuthor? author = null);

    /// <summary>Deletes a discussion entry from the human UI; agent authors are rejected.</summary>
    Task<bool> DeleteCommentAsync(string projectPath, string cardId, string commentId, BoardAuthor author, CancellationToken cancellationToken = default);
    /// <summary>Merges source content and activity into the target, then soft deletes the source atomically.</summary>
    Task<BoardCardRecord?> MergeCardsAsync(string projectPath, string sourceId, string targetId, CancellationToken cancellationToken = default);
    Task<BoardCommentRecord?> AddCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    /// <summary>Compatibility alias: appends to the shared comment stream.</summary>
    Task<BoardCommentRecord?> AddNoteAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardCommentRecord>> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    /// <summary>Ended time, exit code and chat summary for a session, when the Sessions / ChatSummary tables exist in this host; null otherwise.</summary>
    Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<BoardSessionRecord?> LinkSessionAsync(string projectPath, string cardId, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default);
    Task<BoardSessionRecord?> RenameSessionAsync(string projectPath, string cardId, string sessionId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> UnlinkSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    /// <summary>The original link, or the oldest remaining attachment if it was removed. Additional links never change the default.</summary>
    Task<BoardSessionLink?> FindSessionLinkAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Card labels for local history session IDs, across projects, default attachment first. Excludes deleted cards.</summary>
    Task<IReadOnlyList<BoardSessionCard>> GetSessionCardsAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default);

    /// <summary>Returns the supplied sessions with unresolved attention requests, including an Automation's worker.</summary>
    Task<IReadOnlySet<string>> GetAttentionSessionIdsAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default);
    Task<BoardAuthor?> FindSessionAuthorAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardSessionRecord>> GetSessionsForProjectAsync(string projectPath, CancellationToken cancellationToken = default);
    /// <summary>Identifies linked recordings owned by Automation runs, including older native runs.</summary>
    Task<IReadOnlySet<string>> GetAutomationSessionIdsAsync(string projectPath, IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default);

    Task<bool> DeleteAttachmentAsync(string projectPath, string cardId, string attachmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BoardCommitRecord>> GetCommitsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>With a session, atomically link to the target and every attached card in this project; existing links are preserved.</summary>
    Task<BoardCommitRecord?> AddCommitAsync(string projectPath, string cardId, string sha, string author, string message, DateTime committedUtc, SandboxDiffResponse snapshot, CancellationToken cancellationToken = default, string? sessionId = null);
    Task<SandboxDiffResponse?> GetCommitSnapshotAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
    Task<bool> RemoveCommitAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
}
