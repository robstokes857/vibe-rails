using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Looks up Automations with a before-commit trigger and queues them the same way the post-commit
/// hook queues after-commit runs. This never waits on the agent and never blocks Git: VCA is still
/// the only preflight stage that can stop a commit.
/// </summary>
public sealed class AutomatedWorkflowsPreflightStep(IJobStoreAccessor? jobStoreAccessor = null) : IGitPreflightStep
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

        if (jobStoreAccessor is null)
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "No automations are set to run before this commit.",
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

        // Resolve the store only once this non-blocking step is executing. JobStore initializes
        // SQLite in its constructor; resolving it while DI builds the hook runner would let a
        // locked or damaged Jobs database abort the hook before the pipeline can downgrade the
        // failure to a warning.
        var jobStore = jobStoreAccessor.GetRequiredStore();
        var projectPath = context.Snapshot.RepositoryPath;
        var matching = (await jobStore.GetJobsAsync(projectPath, cancellationToken: cancellationToken))
            .Count(job => job.Enabled && job.Triggers.Any(trigger => trigger.Kind == JobTriggerKind.PreCommit));

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

        var runIds = await jobStore.EnqueueEventRunsAsync(
            projectPath,
            JobTriggerKind.PreCommit,
            context.RunId,
            cancellationToken);

        string message;
        if (runIds.Count == 0)
        {
            message = matching == 1
                ? "The matching automation is already running, so it was not queued again."
                : "Matching automations are already running, so none were queued again.";
        }
        else if (runIds.Count == 1)
        {
            message = "Queued 1 automation to run before this commit.";
        }
        else
        {
            message = $"Queued {runIds.Count} automations to run before this commit.";
        }

        if (runIds.Count > 0)
        {
            Log.Information(
                "[Jobs] Queued {Count} before-commit run(s) for {Repository}",
                runIds.Count,
                projectPath);
        }

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
