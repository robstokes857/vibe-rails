namespace VibeRails.Services.Board;

/// <summary>
/// One explicit History row for the settings view. <see cref="AuthorKind"/> and
/// <see cref="AuthorSessionId"/> are the log row's recorded author identity (null for board/lane
/// layout rows), so the service can resolve a generic agent label through its session the same
/// way it does for comments.
/// </summary>
public sealed record BoardHistoryRecord(string Id, string? CardKey, string Kind, string Author, string Body, DateTime CreatedUtc, string? Changes,
    string? AuthorKind = null, string? AuthorSessionId = null);

/// <summary>
/// One board's link to its copy on viberails.ai (VB-51, <c>BoardSyncLinks</c>). <see cref="Cursor"/>
/// is the last server sequence number applied locally; <see cref="LayoutHash"/> is the hash of the
/// lane layout last pushed, so the layout travels only when it changes. <see cref="ProjectPath"/>
/// and <see cref="BoardName"/> come from the board row and let the root scheduler sync every
/// published board without knowing the open project.
/// </summary>
public sealed record BoardSyncLinkRecord(
    string BoardId,
    string RemoteBoardId,
    long Cursor,
    bool Enabled,
    string? LayoutHash,
    DateTime? LastSyncUtc,
    string? LastError,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string ProjectPath = "",
    string BoardName = "",
    string? DestinationKey = null,
    int ActivitySchema = 0,
    string? ActivityAfter = null,
    bool Imported = false,
    string? RemoteKeyPrefix = null);

/// <summary>An unsent Card Log entry together with the card identity the wire needs.</summary>
public sealed record BoardSyncOutboundEntry(BoardCommentRecord Entry, string CardKey);

/// <summary>A rejected entry remains in the local log; status identifies it without copying remote error text.</summary>
public sealed record BoardSyncRejectedEntry(string EntryId, string CardKey, string Kind);

/// <summary>
/// A pulled entry this version can never apply (an unknown kind, a conflicting identity, a card of
/// another board). The pull moves past it, the status view lists it, and the first sync of a newer
/// version tries it again. <see cref="Reason"/> is the desktop's own wording, never remote text.
/// </summary>
public sealed record BoardSyncSkippedEntry(string EntryId, long Seq, string CardKey, string Kind, string Reason);

/// <summary>
/// The remote identity of a pulled Card Log entry. A stamped write stores its log row under this
/// id, at this time, with <c>RemoteSeq</c> already set, so the entry is never pushed back to the
/// server it came from and a second pull of the same entry is recognised by id.
/// </summary>
public sealed record BoardSyncStamp(string EntryId, long RemoteSeq, DateTime CreatedUtc,
    string? Body = null, string? Changes = null, string? BoardId = null);
