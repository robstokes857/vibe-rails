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
        List<string> ruleOutput = [];
        var invocation = context.Request.Invocation;
        // Run before the generic before-commit gate: a blocked STOP must still queue remediation.
        if (context.Request.EnqueueAutomatedJobs && invocation.EnqueueAutomatedJobs
            && invocation.Kind is VcaHookKind.PreCommit or VcaHookKind.CommitMessage
            && !invocation.DemoUi && !invocation.WorkingTreeScope
            && !context.Request.WorkingTreeChanges && !context.Request.UnpushedChanges
            && (launches is not null || jobStoreAccessor is null))
        {
            var violations = context.CompletedSteps?.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id)
                ?.VcaSummary?.Violations ?? [];
            if (violations.Count > 0)
            {
                try
                {
                    var ruleEvents = launches ?? VibeRails.Services.Launches.LaunchEventService.Create();
                    ruleOutput.AddRange(await ruleEvents.EnqueueRuleViolationsAsync(context.Snapshot, invocation.Kind,
                        violations, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ruleOutput.Add("VCA responses could not queue: " + ex.Message + ". Commit enforcement is unchanged.");
                }
            }
        }
        foreach (var line in ruleOutput) await context.WriteOutputAsync(line, null, cancellationToken);

        async Task<GitPreflightStepResult> FinishAsync(GitPreflightStepContext stepContext,
            GitPreflightStepStatus status, string message, CancellationToken ct)
        {
            await stepContext.WriteOutputAsync(message, null, ct);
            // The styled hook console collapses passed/skipped transcripts. A response to a
            // violation (including failed admission) needs visible, non-blocking diagnostics.
            if (ruleOutput.Count > 0) status = GitPreflightStepStatus.Warning;
            return new(Id, DisplayName, status, message, [.. ruleOutput, message], DurationMs: 0, Blocking: false);
        }

        if (context.Request.Invocation.Kind is not VcaHookKind.PreCommit)
        {
            return await FinishAsync(
                context,
                GitPreflightStepStatus.Skipped,
                "Generic before-commit automations do not run at this hook stage.",
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

}
