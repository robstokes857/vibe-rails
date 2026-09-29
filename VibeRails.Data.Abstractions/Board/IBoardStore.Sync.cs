namespace VibeRails.Services.Board;

/// <summary>
/// Board sync with viberails.ai (VB-51). The store owns the ledger: which Card Log entries have
/// been sent (<c>BoardComments.RemoteSeq</c>), how far the server's sequence has been applied
/// (<c>BoardSyncLinks.Cursor</c>), and the stamped writes that apply a pulled entry under its
/// remote id so nothing echoes. The HTTP side lives in the host.
/// </summary>
public partial interface IBoardStore
{
    /// <summary>Explicit history reads for the settings UI and verification, never normal card context.</summary>
    Task<IReadOnlyList<BoardCommentRecord>> GetCardHistoryAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardHistoryRecord>?> GetHistoryAsync(string projectPath, string boardId, string? cardId, int offset, CancellationToken cancellationToken = default);

    /// <summary>The board's link, or null when it was never published.</summary>
    Task<BoardSyncLinkRecord?> GetSyncLinkAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Every link whose board still exists, enabled or not, for the root scheduler and the status view.</summary>
    Task<IReadOnlyList<BoardSyncLinkRecord>> GetSyncLinksAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the link. Returns null, writing nothing, when the board does not exist.</summary>
    Task<BoardSyncLinkRecord?> SaveSyncLinkAsync(BoardSyncLinkRecord link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fixes the project's card key prefix now, if no card has yet, and returns it. A published board
    /// commits to its prefix before the web mints its first key with it.
    /// </summary>
    Task<string> EnsureProjectKeyPrefixAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one <c>created</c> baseline entry, carrying the card's current state at the card's
    /// creation time, for every live card on the board that has no <c>created</c> entry (cards from
    /// before board/14, or cards an older binary created). Returns how many were written. Their
    /// comments and notes are already log rows. Every push calls it; with nothing to write it only reads.
    /// </summary>
    Task<int> WriteSyncBaselineAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsent entries (<c>RemoteSeq</c> NULL) of this board's cards, oldest first, at most
    /// <paramref name="limit"/>. Only cards with a <c>created</c> entry take part, so the server
    /// always sees a card before anything about it.
    /// </summary>
    Task<IReadOnlyList<BoardSyncOutboundEntry>> GetUnsentLogEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default);

    Task<int> CountUnsentLogEntriesAsync(string boardId, CancellationToken cancellationToken = default);

    /// <summary>The greatest acknowledged or pulled sequence already recorded for this board.</summary>
    Task<long> GetMaxAcknowledgedSequenceAsync(string boardId, CancellationToken cancellationToken = default);

    /// <summary>Sets aside one still-unsent entry of this board, retaining its data and protecting its fields until a later local correction is acknowledged.</summary>
    Task<bool> RejectLogEntryAsync(string boardId, string entryId, CancellationToken cancellationToken = default);

    /// <summary>Counts retained rejected rows, including rows whose fields were later corrected.</summary>
    Task<int> CountRejectedLogEntriesAsync(string boardId, CancellationToken cancellationToken = default);

    /// <summary>The latest rejected identities for this board's status view, capped at 50.</summary>
    Task<IReadOnlyList<BoardSyncRejectedEntry>> GetRejectedLogEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a pulled entry the pull moved past because this version can never apply it. False when
    /// the board is gone or the entry was already recorded (the first record is kept).
    /// </summary>
    Task<bool> RecordSkippedSyncEntryAsync(string boardId, BoardSyncSkippedEntry entry, CancellationToken cancellationToken = default);

    Task<int> CountSkippedSyncEntriesAsync(string boardId, CancellationToken cancellationToken = default);

    /// <summary>The latest skipped entries by server sequence for the status view, capped at 50.</summary>
    Task<IReadOnlyList<BoardSyncSkippedEntry>> GetSkippedSyncEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Records the sequence the server gave each entry; 0 sets an entry aside as local-only.</summary>
    Task MarkLogEntriesSentAsync(IReadOnlyList<KeyValuePair<string, long>> sent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what was sent: every entry of the board's cards becomes unsent again, and remote
    /// entries skipped on the old board are forgotten. Used when a re-publish lands on a different
    /// remote board (another account's key), which has none of it.
    /// </summary>
    Task<int> ResetSentMarksAsync(string boardId, CancellationToken cancellationToken = default);

    /// <summary>True when a Card Log row with this id exists, whatever its kind or card.</summary>
    Task<bool> HasLogEntryAsync(string entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The fields named by the card's <c>created</c> / <c>change</c> entries that are later than
    /// server sequence <paramref name="remoteSeq"/>: unsent ones and ones the server numbered
    /// higher, plus fields of rejected entries that have not received a later local correction.
    /// A pulled web change skips these fields so pending edits and rejected local values survive.
    /// </summary>
    Task<IReadOnlySet<string>> GetFieldsChangedAfterAsync(string cardId, long remoteSeq, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a card minted elsewhere under its own id and key. The local number is the web
    /// card's when it is just above the project's high-water mark (a bounded gap, so a corrupt key
    /// cannot jump or exhaust local numbering), else the next local number; the key is stored
    /// exactly as minted. Returns null when a card with that id already exists.
    /// </summary>
    Task<BoardCardRecord?> CreateSyncedCardAsync(string projectPath, string cardId, string cardKey, NewBoardCard card, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default);

    /// <summary>Applies a pulled change, including a lane move through <see cref="BoardCardPatch.ColumnId"/>; the log row carries the stamp.</summary>
    Task<BoardCardRecord?> UpdateSyncedCardAsync(string projectPath, string cardId, BoardCardPatch patch, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the card for a pulled <c>deleted</c> entry; the log row carries the stamp.</summary>
    Task<bool> DeleteSyncedCardAsync(string projectPath, string cardId, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default);

    /// <summary>Stores a pulled comment or note under its remote id.</summary>
    Task<BoardCommentRecord?> AddSyncedCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, string kind, BoardSyncStamp stamp, CancellationToken cancellationToken = default);
}
