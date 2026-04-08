using System.Collections.Concurrent;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Bert;

/// <summary>
/// Per-session capture window coordinator. Runs `git diff --numstat` against
/// the starting commit every time the session goes idle and replaces any
/// previously stored file changes for the active userInputId. The next user
/// input finalizes the previous window (one last diff) before opening a new
/// one, so late LLM writes that slip in between the final idle and the next
/// prompt are still captured under the correct input.
/// </summary>
public sealed class GitDiffCaptureService : IGitDiffCaptureService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GitDiffCaptureService> _logger;
    private readonly ConcurrentDictionary<string, CaptureWindow> _windows = new();

    public GitDiffCaptureService(IServiceProvider serviceProvider, ILogger<GitDiffCaptureService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task BeginCaptureWindowAsync(
        string sessionId,
        long userInputId,
        string? startingCommitHash,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        // Finalize the previous window synchronously so any LLM changes made
        // between its last idle event and now are captured under the OLD
        // userInputId before we switch to the new one.
        if (_windows.TryRemove(sessionId, out var previous))
        {
            await CaptureAsync(previous, cancellationToken);
        }

        _windows[sessionId] = new CaptureWindow
        {
            UserInputId = userInputId,
            StartingCommitHash = startingCommitHash,
            WorkingDirectory = workingDirectory,
        };
    }

    public async Task OnIdleAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_windows.TryGetValue(sessionId, out var window))
            return;

        await CaptureAsync(window, cancellationToken);
    }

    public void EndCaptureWindow(string sessionId)
    {
        _windows.TryRemove(sessionId, out _);
    }

    private async Task CaptureAsync(CaptureWindow window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.StartingCommitHash))
            return;
        if (string.IsNullOrWhiteSpace(window.WorkingDirectory))
            return;

        await window.Gate.WaitAsync(cancellationToken);
        try
        {
            var git = new GitService(window.WorkingDirectory);
            var changes = await git.GetFileChangesSinceAsync(window.StartingCommitHash!, cancellationToken);
            if (changes.Count == 0)
                return;

            // Resolve a fresh repository on every capture so we never hold
            // onto a scoped connection across idle events.
            await using var scope = _serviceProvider.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            await repository.ReplaceFileChangesAsync(window.UserInputId, changes, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[GitDiffCapture] Failed to capture diff for userInputId={UserInputId} in {WorkDir}",
                window.UserInputId, window.WorkingDirectory);
        }
        finally
        {
            window.Gate.Release();
        }
    }

    private sealed class CaptureWindow
    {
        public required long UserInputId { get; init; }
        public required string? StartingCommitHash { get; init; }
        public required string WorkingDirectory { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
