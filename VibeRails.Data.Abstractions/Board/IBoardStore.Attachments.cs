using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public partial interface IBoardStore
{
    /// <param name="author">Who added the file; recorded on the description revision the attachment change produces. Null means the user.</param>
    Task<BoardAttachmentRecord?> AddAttachmentContentAsync(string projectPath, string cardId, string name, string mimeType, byte[] content, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);
}
