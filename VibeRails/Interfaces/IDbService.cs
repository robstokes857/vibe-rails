using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Interfaces
{
    public interface IDbService
    {
        void InitializeDatabase();

        // Session logging for terminal sessions
        Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir);
        Task LogSessionOutputAsync(string sessionId, string content, bool isError = false);
        Task CompleteSessionAsync(string sessionId, int exitCode);

        // Session retrieval
        Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<SessionResponse>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken);

        // User input tracking
        Task<UserInputRecord?> GetLastUserInputAsync(string sessionId);
        Task<long> InsertUserInputAsync(string sessionId, int sequence, string inputText, string? gitCommitHash);
        Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes);
        Task RecordUserInputAsync(string sessionId, string inputText, IGitService gitService, CancellationToken cancellationToken = default);

        // Claude Plan tracking
        Task<long> CreateClaudePlanAsync(string sessionId, long? userInputId, string? planFilePath, string planContent, string? summary);
        Task<ClaudePlanRecord?> GetClaudePlanAsync(long planId, CancellationToken cancellationToken);
        Task<List<ClaudePlanRecord>> GetClaudePlansForSessionAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<ClaudePlanRecord>> GetRecentClaudePlansAsync(int limit, CancellationToken cancellationToken);
        Task UpdateClaudePlanStatusAsync(long planId, string status);
        Task CompleteClaudePlanAsync(long planId);
    }
}
