namespace VibeRails.Services.GitPreflight;

using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Services.VCA.Hooks;

public sealed class AutomatedWorkflowsPreflightStep : IGitPreflightStep
{
    public const string Id = "automated-workflows";

    private readonly IJobStore? _jobStore;
    private readonly JobWorkspaceService _workspaceService;

    public AutomatedWorkflowsPreflightStep(IJobStore? jobStore = null)
        : this(jobStore, new JobWorkspaceService())
    {
    }

    internal AutomatedWorkflowsPreflightStep(IJobStore? jobStore, JobWorkspaceService workspaceService)
    {
        _jobStore = jobStore;
        _workspaceService = workspaceService;
    }

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

        if (_jobStore is null)
        {
            const string unavailableMessage = "Automated Jobs storage is unavailable; commit continues.";
            await context.WriteOutputAsync(unavailableMessage, null, cancellationToken);
            return Result(GitPreflightStepStatus.Warning, unavailableMessage);
        }

        var vca = context.CompletedSteps?.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id);
        var outcome = vca?.Status is GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error
            ? "blocked"
            : "passed";
        JobWorkspaceService.CapturedStagedSnapshot? capturedSnapshot = null;
        var queuePublicationStarted = false;
        try
        {
            // Publish a complete, atomically renamed patch before any Queued row can refer to it.
            // Its immutable base commit is queued alongside the token so workers can reconstruct the
            // exact index tree even when the repository's HEAD advances before they claim the run.
            capturedSnapshot = await _workspaceService.CaptureStagedSnapshotAsync(
                context.Snapshot,
                cancellationToken);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["preflightRunId"] = context.RunId,
                ["outcome"] = outcome,
                ["repositoryPath"] = context.Snapshot.RepositoryPath,
                ["hookKind"] = context.Request.Invocation.Kind.ToString(),
                ["vcaStatus"] = vca?.Status.ToString() ?? "Unknown",
                ["stagedFiles"] = string.Join("\n", context.Snapshot.Files.Select(file => file.RelativePath)),
                [JobWorkspaceService.StagedSnapshotIdContextKey] = capturedSnapshot.Id,
                [JobWorkspaceService.StagedSnapshotBaseTreeContextKey] = capturedSnapshot.BaseTree,
                [JobWorkspaceService.StagedSnapshotTreeContextKey] = capturedSnapshot.StagedTree
            };
            if (capturedSnapshot.BaseCommit is not null)
            {
                metadata[JobWorkspaceService.StagedSnapshotBaseCommitContextKey] = capturedSnapshot.BaseCommit;
            }
            var contextJson = JsonSerializer.Serialize(
                metadata,
                AppJsonSerializerContext.Default.DictionaryStringString);
            queuePublicationStarted = true;
            var runIds = await _jobStore.EnqueueEventRunsAsync(
                context.Snapshot.RepositoryPath,
                JobTriggerKind.Vca,
                context.RunId,
                contextJson,
                cancellationToken);
            if (runIds.Count == 0)
            {
                _workspaceService.DiscardCapturedSnapshot(capturedSnapshot);
            }
            else
            {
                // Per-run copies make ordinary cleanup deterministic. During this post-publish
                // fan-out, workers fall back to the already-complete captured copy named in metadata.
                await _workspaceService.MaterializeRunSnapshotsAsync(
                    capturedSnapshot,
                    runIds,
                    cancellationToken);
            }
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
        finally
        {
            // Serialization or another pre-publication failure cannot strand an unreferenced file.
            // Once enqueue begins it may have committed a subset of runs before throwing, so retain
            // the shared capture and let the DB-aware sweeper decide rather than guessing. Disposing
            // removes the publication marker before releasing the shared cross-process lease.
            if (capturedSnapshot is not null)
            {
                if (!queuePublicationStarted)
                    _workspaceService.DiscardCapturedSnapshot(capturedSnapshot);
                capturedSnapshot.Dispose();
            }
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
