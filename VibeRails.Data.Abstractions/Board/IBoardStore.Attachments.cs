using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public partial interface IBoardStore
{
    Task<BoardAttachmentRecord?> AddAttachmentContentAsync(string projectPath, string cardId, string name, string mimeType, byte[] content, CancellationToken cancellationToken = default);
    /// <summary>Reads only scoped attachment metadata; it never materializes the stored BLOB.</summary>
    Task<BoardAttachmentMetadata?> FindAttachmentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);
    Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);
}
