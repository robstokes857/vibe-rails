using VibeRails.Services.Bert;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Forwards terminal idle + session-complete events to
/// <see cref="IGitDiffCaptureService"/>. All per-session capture state lives
/// in the capture service (a singleton), so this observer holds nothing of
/// its own.
/// </summary>
public sealed class GitDiffIdleCaptureObserver : ITerminalIoObserver
{
    private readonly IGitDiffCaptureService _captureService;

    public GitDiffIdleCaptureObserver(IGitDiffCaptureService captureService)
    {
        _captureService = captureService;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        await _captureService.OnIdleAsync(idleEvent.SessionId, cancellationToken);
    }

    public ValueTask OnSessionCompleteAsync(TerminalSessionCompleteEvent completeEvent, CancellationToken cancellationToken = default)
    {
        _captureService.EndCaptureWindow(completeEvent.SessionId);
        return ValueTask.CompletedTask;
    }
}
