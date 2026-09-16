using VibeRails.DTOs;
namespace VibeRails.Services.Board;
public partial interface IBoardStore
{
    Task<BoardDescriptionHistoryResponse?> GetDescriptionHistoryAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<bool> RecordDescriptionSessionAsync(string projectPath, string cardId, int revision, string sessionId, string kind, CancellationToken cancellationToken = default);
}
