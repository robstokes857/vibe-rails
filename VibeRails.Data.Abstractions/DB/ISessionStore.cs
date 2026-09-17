using VibeRails.DTOs;

namespace VibeRails.DB;

public interface ISessionStore
{
    Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir, int ownerPid, string? jobRunId = null);

    Task LogSessionOutputAsync(string sessionId, byte[] content, bool isError = false);

    Task CompleteSessionAsync(string sessionId, int exitCode);

    Task<(string? Cli, string? DisplayName)> GetSessionDisplayInfoAsync(string sessionId);

    Task SetParentSessionIdAsync(string sessionId, string parentSessionId);

    Task SetSessionDisplayNameAsync(string sessionId, string displayName);

    Task<SessionResponse?> GetSessionByIdAsync(string sessionId, CancellationToken cancellationToken);

    Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken);

    Task<List<SessionResponse>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken);

    Task<SessionOutputDetailResponse?> GetSessionOutputAsync(string sessionId, CancellationToken cancellationToken);

    Task<List<string>> GetEndedUnprocessedSessionIdsAsync(int limit, CancellationToken cancellationToken);

    Task<List<SessionLogChunkRecord>> GetSessionLogChunksAsync(string sessionId, CancellationToken cancellationToken);

    Task InsertTerminalSessionLogAsync(string sessionId, int sequence, byte[] data, bool isAlternateScreen, int cols, int rows);

    /// <summary>
    /// Writes a drain's worth of legacy and enriched output rows in one write transaction, in the
    /// order given. This is the hot path for live terminals: one lock acquisition per batch instead
    /// of one per row keeps streaming agents from starving every other writer on the shared file.
    /// </summary>
    Task PersistTerminalOutputAsync(string sessionId, IReadOnlyList<TerminalOutputWrite> rows, CancellationToken cancellationToken = default);

    Task<List<TerminalSessionLogRecord>> GetTerminalSessionLogsAsync(string sessionId, CancellationToken cancellationToken);

    Task SaveSessionOutputAndMarkProcessedAsync(string sessionId, string text, CancellationToken cancellationToken);

    Task<ChatHistoryItem?> GetChatHistoryItemAsync(string sessionId, CancellationToken cancellationToken);

    Task<List<ChatHistoryItem>> GetChatHistoryPageAsync(int limit, int offset, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken);

    Task<bool> UpdateChatHistorySessionNameAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken);

    Task<bool> DeleteChatHistorySessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<List<OpenSessionCleanupCandidate>> GetOpenSessionCleanupCandidatesAsync(DateTime trackedCutoff, DateTime untrackedCutoff, CancellationToken cancellationToken);
}
