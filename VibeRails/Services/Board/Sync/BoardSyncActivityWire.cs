namespace VibeRails.Services.Board.Sync;

/// <summary>A desktop-owned replacement snapshot. No machine paths, tab ids or launch settings.</summary>
public sealed record BoardSyncActivityWire(int Schema,
    List<BoardSyncSessionWire> Sessions, List<BoardSyncCommitWire> Commits,
    List<BoardSyncAttachmentWire> Attachments, List<BoardSyncLinkedCardWire> LinkedCards,
    List<string> Warnings);
public sealed record BoardSyncSessionWire(string Id, string DisplayName, string Cli, string Origin,
    DateTime CreatedUtc, bool IsAutomation);
public sealed record BoardSyncCommitWire(string Sha, string Author, string Message, DateTime CommittedUtc,
    List<BoardSyncCommitFileWire> Files);
public sealed record BoardSyncCommitFileWire(string Path, string Status, string? Before, string? After);
public sealed record BoardSyncAttachmentWire(string Id, string Name, string MimeType, long Bytes,
    DateTime CreatedUtc, string? ContentBase64, string? UnavailableReason);
public sealed record BoardSyncLinkedCardWire(string Id, string Key, string DisplayId, string Title, string BoardId);
public sealed record BoardSyncActivityAck(int Schema, string CardId);
