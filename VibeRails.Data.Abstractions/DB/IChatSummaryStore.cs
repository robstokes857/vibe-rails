using VibeRails.DTOs;

namespace VibeRails.DB;

public interface IChatSummaryStore
{
    Task<ChatSummary> SaveChatSummaryAsync(ChatSummary chatSummary, CancellationToken cancellationToken = default);

    Task<ChatSummary?> GetChatSummaryByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<List<ChatSummary>> GetChatSummariesBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<List<ChatSummary>> GetAllChatSummariesAsync(CancellationToken cancellationToken = default);

    Task DeleteChatSummaryAsync(int id, CancellationToken cancellationToken = default);

    Task DeleteChatSummaryBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
