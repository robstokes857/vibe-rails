using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public partial interface IBoardStore
{
    Task<BoardAttachmentRecord?> AddAttachmentContentAsync(string projectPath, string cardId, string name, string mimeType, byte[] content, CancellationToken cancellationToken = default);
    Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);
}
