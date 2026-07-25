namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Formerly captured a staged snapshot and enqueued pre-commit "VCA" Jobs. That trigger — and its
/// throwaway-clone / staged-snapshot machinery — was removed: Jobs now run on a schedule, after a
/// successful commit (via the post-commit hook), or on demand. This step is kept as a no-op so
/// existing preflight pipelines still resolve it without changing their step order.
/// </summary>
public sealed class AutomatedWorkflowsPreflightStep : IGitPreflightStep
{
    public const string Id = "automated-workflows";

    public string StepId => Id;
    public string DisplayName => "Automated workflows";
    public bool CanBlock => false;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        const string message = "Automated Jobs run after a successful commit, not during pre-commit.";
        await context.WriteOutputAsync(message, null, cancellationToken);
        return new GitPreflightStepResult(
            Id,
            DisplayName,
            GitPreflightStepStatus.Skipped,
            message,
            [message],
            DurationMs: 0,
            Blocking: false);
    }
}
