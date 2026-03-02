using VibeRails.DTOs;

namespace VibeRails.Services.Bert;

public interface IBertInputCaptureService
{
    Task CaptureAsync(
        string sessionId,
        long userInputId,
        string inputText,
        string? gitCommitHash,
        IReadOnlyList<FileChangeInfo> fileChanges,
        CancellationToken cancellationToken = default);
}
