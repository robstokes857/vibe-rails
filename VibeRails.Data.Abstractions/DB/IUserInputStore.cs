using VibeRails.DTOs;

namespace VibeRails.DB;

public interface IUserInputStore
{
    Task<List<UserInputRecord>> GetUserInputsForSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<UserInputRecord?> GetLastUserInputAsync(string sessionId);

    Task<UserInputRecord?> GetUserInputByIdAsync(long userInputId, CancellationToken cancellationToken = default);

    Task<long> InsertUserInputAsync(string sessionId, int sequence, string inputText, string? gitCommitHash);

    Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes);

    Task ReplaceFileChangesAsync(long userInputId, List<FileChangeInfo> changes, CancellationToken cancellationToken = default);

    Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<string> GetTextForInputIdOrRawAsync(long inputId, int? maxChars = null, CancellationToken cancellationToken = default);

    Task<string> GetFirstInputTextForSessionOrRawAsync(string sessionId, int? maxChars = null, CancellationToken cancellationToken = default);
}
