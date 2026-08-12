using VibeRails.DTOs;

namespace VibeRails.Services.Environments.Steps;

/// <summary>
/// Thrown when a pre-launch step exits non-zero, times out, or is cancelled. There is deliberately
/// no per-step "continue on failure" override: a step exists to make the launch's preconditions
/// true, so a launch that proceeds without them is worse than one that does not happen.
///
/// Thrown from inside <c>TerminalRunner.CreateSessionAsync</c> so it lands in that method's
/// existing startup-rollback catch, which already disposes the remote connection and the terminal
/// and closes the DB session. There is nothing extra to unwind.
/// </summary>
public sealed class EnvironmentStepFailedException : Exception
{
    public EnvironmentStepFailedException(StepRunSummary summary, string? environmentName)
        : base(BuildMessage(summary, environmentName))
    {
        Summary = summary;
        EnvironmentName = environmentName;
    }

    public StepRunSummary Summary { get; }
    public string? EnvironmentName { get; }
    public EnvironmentStep? FailedStep => Summary.FailedStep;

    private static string BuildMessage(StepRunSummary summary, string? environmentName)
    {
        var scope = string.IsNullOrWhiteSpace(environmentName) ? "this environment" : $"environment \"{environmentName}\"";
        return summary.FailedStep is null
            ? $"A pre-launch step for {scope} failed, so the launch was aborted."
            : $"{summary.FailureMessage} The launch of {scope} was aborted.";
    }
}
