using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Looks up Automations with a before-commit trigger and queues them the same way the post-commit
/// hook queues after-commit runs. This never waits on the agent and never blocks Git: VCA is still
/// the only preflight stage that can stop a commit.
/// </summary>
public sealed class AutomatedWorkflowsPreflightStep(
    IJobStoreAccessor? jobStoreAccessor = null,
    VibeRails.Services.Launches.LaunchEventService? launches = null) : IGitPreflightStep
{
    public const string Id = "automated-workflows";

    public string StepId => Id;
    public string DisplayName => "Automated workflows";
    public bool CanBlock => false;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.Invocation.Kind is not VcaHookKind.PreCommit)
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "Automated workflows run from the pre-commit hook, not here.",
                cancellationToken);
        }

        if (VcaBlocked(context))
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "VCA blocked this commit, so before-commit automations were not started.",
                cancellationToken);
        }

        if (context.Request.Invocation.DemoUi)
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "Preview does not start automations.",
                cancellationToken);
        }

        if (launches is null && jobStoreAccessor is not null)
            return await FinishAsync(context, GitPreflightStepStatus.Skipped, "Legacy Automations are retired.", cancellationToken);
        var events = launches ?? VibeRails.Services.Launches.LaunchEventService.Create();
        var projectPath = context.Snapshot.RepositoryPath;
        var matching = await events.CountAsync(projectPath, JobTriggerKind.PreCommit, cancellationToken);

        if (matching == 0)
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "No automations are set to run before this commit.",
                cancellationToken);
        }

        if (!context.Request.EnqueueAutomatedJobs)
        {
            var preview = matching == 1
                ? "1 automation is set to run before a git commit. This preview does not start it."
                : $"{matching} automations are set to run before a git commit. This preview does not start them.";
            return await FinishAsync(context, GitPreflightStepStatus.Skipped, preview, cancellationToken);
        }

        var count = await events.EnqueueAsync(projectPath, JobTriggerKind.PreCommit, context.RunId, cancellationToken);
        var message = $"Queued {count} before-commit Agent/Action runs. Execution continues asynchronously.";

        return await FinishAsync(context, GitPreflightStepStatus.Passed, message, cancellationToken);
    }

    private static bool VcaBlocked(GitPreflightStepContext context)
    {
        var vca = context.CompletedSteps?.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id);
        return vca is { Status: GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error };
    }

    private async Task<GitPreflightStepResult> FinishAsync(
        GitPreflightStepContext context,
        GitPreflightStepStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        await context.WriteOutputAsync(message, null, cancellationToken);
        return new GitPreflightStepResult(
            Id,
            DisplayName,
            status,
            message,
            [message],
            DurationMs: 0,
            Blocking: false);
    }
}
