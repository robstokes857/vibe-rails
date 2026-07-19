namespace VibeRails.Services.GitPreflight;

using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.VCA.Hooks;

public sealed class AutomatedWorkflowsPreflightStep(IJobStore? jobStore = null) : IGitPreflightStep
{
    public const string Id = "automated-workflows";

    public string StepId => Id;
    public string DisplayName => "Automated workflows";
    public bool CanBlock => false;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.Invocation.Kind is VcaHookKind.CommitMessage
            or VcaHookKind.AcknowledgeCommitMessage)
        {
            const string skippedMessage = "Automated workflows already ran during pre-commit; commit-message step skipped.";
            await context.WriteOutputAsync(skippedMessage, null, cancellationToken);
            return new GitPreflightStepResult(
                Id,
                DisplayName,
                GitPreflightStepStatus.Skipped,
                skippedMessage,
                [skippedMessage],
                DurationMs: 0,
                Blocking: false);
        }

        if (!context.Request.EnqueueAutomatedJobs)
        {
            const string previewMessage = "Automated Jobs are not queued from preview checks.";
            await context.WriteOutputAsync(previewMessage, null, cancellationToken);
            return Result(GitPreflightStepStatus.Skipped, previewMessage);
        }

        if (jobStore is null)
        {
            const string unavailableMessage = "Automated Jobs storage is unavailable; commit continues.";
            await context.WriteOutputAsync(unavailableMessage, null, cancellationToken);
            return Result(GitPreflightStepStatus.Warning, unavailableMessage);
        }

        var vca = context.CompletedSteps?.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id);
        var outcome = vca?.Status is GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error
            ? "blocked"
            : "passed";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["preflightRunId"] = context.RunId,
            ["outcome"] = outcome,
            ["repositoryPath"] = context.Snapshot.RepositoryPath,
            ["hookKind"] = context.Request.Invocation.Kind.ToString(),
            ["vcaStatus"] = vca?.Status.ToString() ?? "Unknown",
            ["stagedFiles"] = string.Join("\n", context.Snapshot.Files.Select(file => file.RelativePath))
        };

        try
        {
            var contextJson = JsonSerializer.Serialize(
                metadata,
                AppJsonSerializerContext.Default.DictionaryStringString);
            var runIds = await jobStore.EnqueueEventRunsAsync(
                context.Snapshot.RepositoryPath,
                JobTriggerKind.Vca,
                context.RunId,
                contextJson,
                cancellationToken);
            var message = runIds.Count == 0
                ? $"No enabled VCA Jobs matched this {outcome} check."
                : $"Queued {runIds.Count} VCA Job(s) for this {outcome} check.";
            await context.WriteOutputAsync(message, null, cancellationToken);
            return Result(GitPreflightStepStatus.Passed, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Could not queue VCA Jobs; commit continues: {ex.Message}";
            await context.WriteOutputAsync(message, null, cancellationToken);
            return Result(GitPreflightStepStatus.Warning, message);
        }

        GitPreflightStepResult Result(GitPreflightStepStatus status, string message) => new(
            Id,
            DisplayName,
            status,
            message,
            [message],
            DurationMs: 0,
            Blocking: false);
    }
}
