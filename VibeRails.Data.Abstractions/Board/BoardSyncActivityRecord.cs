namespace VibeRails.Services.Board;

public sealed record BoardSyncActivityRecord(
    IReadOnlyList<BoardSessionRecord> Sessions,
    IReadOnlyList<BoardCommitRecord> Commits,
    IReadOnlyList<BoardAttachmentMetadata> Attachments,
    IReadOnlyList<BoardLinkedCardRecord> LinkedCards);
