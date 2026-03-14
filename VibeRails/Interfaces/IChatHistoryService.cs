using VibeRails.DTOs;

namespace VibeRails.Interfaces;

public interface IChatHistoryService
{
    Task<ChatHistoryResponse> GetHistoryAsync(int page, int pageSize, CancellationToken cancellationToken);
}
