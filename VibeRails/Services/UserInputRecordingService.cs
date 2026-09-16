using Microsoft.Extensions.Logging;
using Serilog;
using VibeRails.DB;
using VibeRails.Services.BertBaseClasses;
namespace VibeRails.Services;

/// <summary>Captures application Git state, persists input, and starts its diff observation window.</summary>
public sealed class UserInputRecordingService(IUserInputStore repository, IGitDiffCaptureService? gitDiffCaptureService = null, ILogger<UserInputRecordingService>? logger = null)
{
    private readonly IUserInputStore _repository = repository;
    private readonly IGitDiffCaptureService? _gitDiffCaptureService = gitDiffCaptureService;
    private readonly ILogger<UserInputRecordingService>? _logger = logger;
    public async Task RecordAsync(string sessionId, string inputText, IGitService gitService, CancellationToken cancellationToken = default)
    {
        try
        {
            var currentCommitHash = await gitService.GetCurrentCommitHashAsync(cancellationToken);

            var lastInput = await _repository.GetLastUserInputAsync(sessionId);
            var sequence = (lastInput?.Sequence ?? 0) + 1;

            var userInputId = await _repository.InsertUserInputAsync(sessionId, sequence, inputText, currentCommitHash);

            // Open a git-diff capture window for this input. The idle
            // observer re-runs the diff every time the session goes idle
            // and replaces the stored file changes, until the next user
            // input finalizes the window. The previous input's window
            // (if any) is finalized synchronously inside
            // BeginCaptureWindowAsync so late writes are captured against
            // the correct userInputId.
            if (_gitDiffCaptureService != null)
            {
                var workingDirectory = await _repository.GetSessionWorkingDirectoryAsync(sessionId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    await _gitDiffCaptureService.BeginCaptureWindowAsync(
                        sessionId,
                        userInputId,
                        currentCommitHash,
                        workingDirectory,
                        cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                _logger.LogWarning(ex, "[VibeRails] Error recording user input for session {SessionId}", sessionId);
            }
            else
            {
                Log.Warning(ex, "[VibeRails] Error recording user input for session {SessionId}", sessionId);
            }
        }
    }

}
