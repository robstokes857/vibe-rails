using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.UserInOut;

namespace VibeRails.DB
{
    public interface IRepository
    {
        void InitializeDatabase();

        // Session lifecycle
        Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir, int ownerPid);
        Task LogSessionOutputAsync(string sessionId, byte[] content, bool isError = false);
        Task CompleteSessionAsync(string sessionId, int exitCode);
        Task<string> GetProjectDisplayNameAsync(string path, CancellationToken cancellationToken = default);
        Task<bool> UpdateLatestProjectDisplayNameAsync(string path, string projectDisplayName, CancellationToken cancellationToken = default);
        Task<(string? Cli, string? DisplayName)> GetSessionDisplayInfoAsync(string sessionId);
        Task SetParentSessionIdAsync(string sessionId, string parentSessionId);
        Task SetSessionDisplayNameAsync(string sessionId, string displayName);

        // Session retrieval
        Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<SessionResponse>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken);
        Task<SessionOutputDetailResponse?> GetSessionOutputAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<string>> GetEndedUnprocessedSessionIdsAsync(int limit, CancellationToken cancellationToken);
        Task<List<SessionLogChunkRecord>> GetSessionLogChunksAsync(string sessionId, CancellationToken cancellationToken);
        Task InsertTerminalSessionLogAsync(string sessionId, int sequence, byte[] data, bool isAlternateScreen, int cols, int rows);
        Task<List<TerminalSessionLogRecord>> GetTerminalSessionLogsAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<UserInputRecord>> GetUserInputsForSessionAsync(string sessionId, CancellationToken cancellationToken);
        Task SaveSessionOutputAndMarkProcessedAsync(string sessionId, string text, CancellationToken cancellationToken);
        Task<ChatHistoryItem?> GetChatHistoryItemAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<ChatHistoryItem>> GetChatHistoryPageAsync(int limit, int offset, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken);
        Task<bool> UpdateChatHistorySessionNameAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken);
        Task<bool> DeleteChatHistorySessionAsync(string sessionId, CancellationToken cancellationToken);
        Task<List<OpenSessionCleanupCandidate>> GetOpenSessionCleanupCandidatesAsync(DateTime trackedCutoff, DateTime untrackedCutoff, CancellationToken cancellationToken);

        // User input tracking
        Task<UserInputRecord?> GetLastUserInputAsync(string sessionId);
        Task<UserInputRecord?> GetUserInputByIdAsync(long userInputId, CancellationToken cancellationToken = default);
        Task<long> InsertUserInputAsync(string sessionId, int sequence, string inputText, string? gitCommitHash);
        Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes);
        Task ReplaceFileChangesAsync(long userInputId, List<FileChangeInfo> changes, CancellationToken cancellationToken = default);
        Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default);
        Task RecordUserInputAsync(string sessionId, string inputText, IGitService gitService, CancellationToken cancellationToken = default);
        Task InsertTuiEventAsync(string sessionId, DateTimeOffset timestampUtc, string triggerString, TUI_Event_Watcher_Type eventType, CancellationToken cancellationToken = default);

        // Cleaned user input (1:1 with UserInputs via dual FKs)
        Task<long> CreateCleanedAndLinkAsync(string sessionId, long userInputId, string cleanedText, DateTime createdUtc, CancellationToken cancellationToken = default);
        Task UpdateCleanedTextAsync(long userInputId, string cleanedText, CancellationToken cancellationToken = default);
        Task<bool> IsInputCleanedAsync(long userInputId, CancellationToken cancellationToken = default);
        Task<string?> GetCleanedTextForInputIdAsync(long inputId, CancellationToken cancellationToken = default);
        Task<List<string>> GetSessionCleanedTextOrderedAsync(string sessionId, CancellationToken cancellationToken = default);

        // Cleaned-or-raw (backs IGetUserText)
        Task<string> GetTextForInputIdOrRawAsync(long inputId, int? maxChars = null, CancellationToken cancellationToken = default);
        Task<string> GetFirstInputTextForSessionOrRawAsync(string sessionId, int? maxChars = null, CancellationToken cancellationToken = default);
        Task<List<UserInputRecord>> GetUncleanedInputsForSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<List<UserInputRecord>> GetUncleanedInputsForClosedSessionsAsync(int batchSize, CancellationToken cancellationToken = default);
        Task<List<UserInputRecord>> GetPromptPrefixedCleanedInputsForClosedSessionsAsync(int batchSize, CancellationToken cancellationToken = default);

        // BERT embedding tracking
        Task<List<UnembeddedCleanedInput>> GetUnembeddedCleanedInputsAsync(int batchSize, CancellationToken cancellationToken = default);
        Task MarkCleanedInputEmbeddedAsync(long cleanedId, CancellationToken cancellationToken = default);

        // Environment operations (global, not project-scoped)
        Task<LLM_Environment?> GetEnvironmentByNameAndLlmAsync(string name, LLM llm, CancellationToken cancellationToken = default);
        Task<LLM_Environment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default);
        Task<LLM_Environment> GetOrCreateEnvironmentAsync(string name, LLM llm, CancellationToken cancellationToken = default);
        Task<List<LLM_Environment>> GetAllEnvironmentsAsync(CancellationToken cancellationToken = default);
        Task<List<LLM_Environment>> GetCustomEnvironmentsAsync(CancellationToken cancellationToken = default);
        Task<LLM_Environment> SaveEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);
        Task UpdateEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);
        Task DeleteEnvironmentAsync(int id, CancellationToken cancellationToken = default);

        // Sandbox operations (project-scoped)
        Task<Sandbox> SaveSandboxAsync(Sandbox sandbox, CancellationToken cancellationToken = default);
        Task<List<Sandbox>> GetSandboxesByProjectAsync(string projectPath, CancellationToken cancellationToken = default);
        Task<Sandbox?> GetSandboxByIdAsync(int id, CancellationToken cancellationToken = default);
        Task<Sandbox?> GetSandboxByNameAndProjectAsync(string name, string projectPath, CancellationToken cancellationToken = default);
        Task DeleteSandboxAsync(int id, CancellationToken cancellationToken = default);

        // Agent metadata operations
        Task<string?> GetAgentCustomNameAsync(string path, CancellationToken cancellationToken = default);
        Task SetAgentCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default);

        // ProjectCache operations (key-value store per project)
        Task<string?> GetProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default);
        Task SetProjectCacheValueAsync(string projectPath, string key, string value, CancellationToken cancellationToken = default);
        Task<Dictionary<string, string>> GetAllProjectCacheAsync(string projectPath, CancellationToken cancellationToken = default);
        Task RemoveProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default);

        // ChatSummary operations
        Task<ChatSummary> SaveChatSummaryAsync(ChatSummary chatSummary, CancellationToken cancellationToken = default);
        Task<ChatSummary?> GetChatSummaryByIdAsync(int id, CancellationToken cancellationToken = default);
        Task<List<ChatSummary>> GetChatSummariesBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<List<ChatSummary>> GetAllChatSummariesAsync(CancellationToken cancellationToken = default);
        Task DeleteChatSummaryAsync(int id, CancellationToken cancellationToken = default);
        Task DeleteChatSummaryBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
    }
}
