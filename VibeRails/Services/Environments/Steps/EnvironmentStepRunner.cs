using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;

namespace VibeRails.Services.Environments.Steps;

/// <summary>
/// Thin by design: load the phase's enabled steps, run them one at a time through
/// <see cref="ICliWrapper.RunInNewTerminalAsync"/>, stop at the first non-zero exit. Every piece of
/// process mechanics — the temp script, the sentinel race, the timeout kill, the terminal-emulator
/// discovery — lives in the wrapper, which is what makes this unit-testable against a fake
/// <see cref="ICliWrapper"/> with no processes involved at all.
/// </summary>
public sealed class EnvironmentStepRunner : IEnvironmentStepRunner
{
    private readonly IRepository _repository;
    private readonly ICliWrapper _cli;

    public EnvironmentStepRunner(IRepository repository, ICliWrapper cli)
    {
        _repository = repository;
        _cli = cli;
    }

    public async Task<StepRunSummary> RunPhaseAsync(
        int environmentId,
        EnvironmentStepPhase phase,
        string workingDirectory,
        Func<StepProgress, ValueTask>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // Enabled-only, Position-ordered, straight from SQL — the runner never re-sorts or
        // re-filters, so what runs is exactly what the editor showed.
        var steps = await _repository.GetEnabledStepsAsync(environmentId, phase, cancellationToken);
        if (steps.Count == 0)
            return StepRunSummary.Empty;

        Log.Information(
            "[Steps] Running {Count} {Phase} step(s) for environment {EnvironmentId} in {WorkingDirectory}",
            steps.Count,
            phase,
            environmentId,
            workingDirectory);

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            await ReportAsync(onProgress, new StepProgress(index, steps.Count, step.DisplayName, StepProgressKind.Started, null));

            var result = await _cli.RunInNewTerminalAsync(
                new CliTerminalRequest(
                    ScriptBody: step.Command,
                    WorkingDirectory: workingDirectory,
                    StartMinimized: step.StartMinimized,
                    Timeout: TimeSpan.FromSeconds(ClampTimeout(step.TimeoutSeconds))),
                cancellationToken);

            if (!result.IsSuccess)
            {
                Log.Warning(
                    "[Steps] {Phase} step \"{Step}\" for environment {EnvironmentId} {Outcome}; stopping the phase",
                    phase,
                    step.DisplayName,
                    environmentId,
                    result.DescribeFailure());

                await ReportAsync(
                    onProgress,
                    new StepProgress(index, steps.Count, step.DisplayName, StepProgressKind.Failed, result.ExitCode));

                return new StepRunSummary(Success: false, StepsRun: index + 1, step, result);
            }

            await ReportAsync(
                onProgress,
                new StepProgress(index, steps.Count, step.DisplayName, StepProgressKind.Succeeded, result.ExitCode));
        }

        return new StepRunSummary(Success: true, StepsRun: steps.Count, FailedStep: null, FailedResult: null);
    }

    /// <summary>
    /// The API clamps on write, but a row written by an older build (or edited in the DB by hand)
    /// must not produce a zero or negative timeout that would fail every step instantly.
    /// </summary>
    private static int ClampTimeout(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds, EnvironmentStep.MinTimeoutSeconds, EnvironmentStep.MaxTimeoutSeconds);

    /// <summary>
    /// Progress reporting is advisory. A UI callback that throws must not take the launch down with
    /// it — the step itself already succeeded or failed on its own terms.
    /// </summary>
    private static async ValueTask ReportAsync(Func<StepProgress, ValueTask>? onProgress, StepProgress progress)
    {
        if (onProgress is null) return;

        try
        {
            await onProgress(progress);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Steps] Progress callback failed for step \"{Step}\"", progress.Name);
        }
    }
}
