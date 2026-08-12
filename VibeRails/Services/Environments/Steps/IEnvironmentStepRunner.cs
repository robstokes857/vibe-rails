using VibeRails.DTOs;
using VibeRails.Services.Cli;

namespace VibeRails.Services.Environments.Steps;

public enum StepProgressKind
{
    Started = 0,
    Succeeded = 1,
    Failed = 2
}

/// <param name="Index">0-based position within the phase being run.</param>
/// <param name="Total">How many enabled steps this phase has.</param>
public sealed record StepProgress(int Index, int Total, string Name, StepProgressKind Kind, int? ExitCode);

/// <param name="StepsRun">How many steps were started — including the one that failed.</param>
public sealed record StepRunSummary(bool Success, int StepsRun, EnvironmentStep? FailedStep, CliResult? FailedResult)
{
    public static readonly StepRunSummary Empty = new(Success: true, StepsRun: 0, FailedStep: null, FailedResult: null);

    /// <summary>Toast/log text for a failure. Empty string when nothing failed.</summary>
    public string FailureMessage =>
        FailedStep is null
            ? string.Empty
            : $"Step \"{FailedStep.DisplayName}\" {FailedResult?.DescribeFailure() ?? "failed"}.";
}

public interface IEnvironmentStepRunner
{
    /// <summary>
    /// Runs every enabled step for (<paramref name="environmentId"/>, <paramref name="phase"/>) in
    /// Position order, one at a time, each blocking until it finishes. Stops at the first non-zero
    /// exit and reports which step it was.
    /// </summary>
    /// <param name="workingDirectory">
    /// The already-workspace-resolved directory, so a Persistent/PerRun environment runs its steps
    /// inside the clone rather than the project root.
    /// </param>
    Task<StepRunSummary> RunPhaseAsync(
        int environmentId,
        EnvironmentStepPhase phase,
        string workingDirectory,
        Func<StepProgress, ValueTask>? onProgress = null,
        CancellationToken cancellationToken = default);
}
