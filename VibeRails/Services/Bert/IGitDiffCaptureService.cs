namespace VibeRails.Services.Bert;

/// <summary>
/// Coordinates per-session git-diff capture windows. A window opens when a
/// user input is recorded and closes when the next user input arrives or the
/// session completes. While a window is open, each terminal-idle event
/// triggers a fresh git diff against the starting commit and replaces any
/// previously stored InputFileChanges for that userInputId.
/// </summary>
public interface IGitDiffCaptureService
{
    Task BeginCaptureWindowAsync(
        string sessionId,
        long userInputId,
        string? startingCommitHash,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task OnIdleAsync(string sessionId, CancellationToken cancellationToken = default);

    void EndCaptureWindow(string sessionId);
}
