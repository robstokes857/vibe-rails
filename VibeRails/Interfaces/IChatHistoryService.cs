using VibeRails.DTOs;

namespace VibeRails.Interfaces;

public interface IChatHistoryService
{
    Task<ChatHistoryResponse> GetHistoryAsync(int page, int pageSize, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken);
    Task<ChatHistoryItem?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<bool> RenameSessionAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken);
    Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<ChatSummaryResponse> GetSummaryAsync(string sessionId, bool regenerate, CancellationToken cancellationToken);

    /// <summary>
    /// Send to (VIBE-363): writes a handoff from the text the browser rendered by replaying the
    /// session's whole recording. Reads and writes no cached transcript or summary.
    /// Throws KeyNotFoundException for an unknown session.
    /// </summary>
    Task<SessionHandoffResponse> GetHandoffAsync(string sessionId, string renderedSession, CancellationToken cancellationToken);
}
