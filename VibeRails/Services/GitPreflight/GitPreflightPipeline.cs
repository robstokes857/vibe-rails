using System.Diagnostics;

namespace VibeRails.Services.GitPreflight;

public sealed class GitPreflightPipeline : IGitPreflightPipeline
{
    private static readonly string[] StepOrder =
    [
        VcaPreflightStep.Id,
        MintLintPreflightStep.Id,
        AutomatedWorkflowsPreflightStep.Id
    ];

    private readonly IGitStagedSnapshotProvider _snapshotProvider;
    private readonly IGitWorkingTreeSnapshotProvider _workingTreeSnapshotProvider;
    private readonly IReadOnlyList<IGitPreflightStep> _steps;

    public GitPreflightPipeline(
        IGitStagedSnapshotProvider snapshotProvider,
        IGitWorkingTreeSnapshotProvider workingTreeSnapshotProvider,
        IEnumerable<IGitPreflightStep> steps)
    {
        _snapshotProvider = snapshotProvider;
        _workingTreeSnapshotProvider = workingTreeSnapshotProvider;
        var byId = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        _steps = StepOrder.Select(id => byId.TryGetValue(id, out var step)
            ? step
            : throw new InvalidOperationException($"Git preflight step '{id}' is not registered.")).ToList();
    }

    public async Task<GitPreflightRunResult> RunAsync(
        GitPreflightRequest request,
        Func<GitPreflightEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = Guid.NewGuid().ToString("N");
        var sequence = 0L;
        var runTimer = Stopwatch.StartNew();

        async ValueTask EmitAsync(
            GitPreflightEventType type,
            string? stepId,
            GitPreflightStepStatus status,
            string message,
            IReadOnlyDictionary<string, string>? details = null,
            long? durationMs = null,
            bool blocking = false,
            bool? commitAllowed = null,
            int? stepNumber = null,
            CancellationToken emitCancellationToken = default)
        {
            if (eventSink == null)
            {
                return;
            }

            await eventSink(new GitPreflightEvent(
                runId,
                ++sequence,
                DateTimeOffset.UtcNow,
                type,
                stepId,
                status,
                message,
                details,
                durationMs,
                blocking,
                commitAllowed,
                stepNumber,
                _steps.Count), emitCancellationToken);
        }

        GitStagedSnapshot snapshot;
        try
        {
            snapshot = request.Invocation.DemoUi
                ? GitStagedSnapshot.Preview(request.WorkingDirectory)
                : request.WorkingTreeChanges
                    ? await _workingTreeSnapshotProvider.CaptureWorkingTreeAsync(
                        request.WorkingDirectory, cancellationToken)
                    : await _snapshotProvider.CaptureAsync(
                        request.WorkingDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CreateCancelledResult(runId, request.WorkingDirectory, [], runTimer.ElapsedMilliseconds);
            await EmitAsync(
                GitPreflightEventType.RunFinished,
                null,
                GitPreflightStepStatus.Cancelled,
                "Git preflight cancelled.",
                durationMs: cancelled.DurationMs,
                commitAllowed: false,
                emitCancellationToken: CancellationToken.None);
            return cancelled;
        }
        catch (Exception ex)
        {
            var failed = new GitPreflightRunResult(
                runId,
                Path.GetFullPath(request.WorkingDirectory),
                [],
                [],
                GitPreflightStepStatus.Error,
                CommitAllowed: false,
                runTimer.ElapsedMilliseconds);
            await EmitAsync(
                GitPreflightEventType.RunFinished,
                null,
                GitPreflightStepStatus.Error,
                $"Unable to read the staged Git snapshot: {ex.Message}",
                durationMs: failed.DurationMs,
                blocking: true,
                commitAllowed: false,
                emitCancellationToken: cancellationToken);
            return failed;
        }

        await EmitAsync(
            GitPreflightEventType.RunStarted,
            null,
            GitPreflightStepStatus.Running,
            $"Checking {snapshot.Files.Count} staged file(s).",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repositoryPath"] = snapshot.RepositoryPath,
                ["stagedFileCount"] = snapshot.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["stagedFiles"] = string.Join("\n", snapshot.Files.Select(file => file.RelativePath)),
                ["hookKind"] = request.Invocation.Kind.ToString()
            },
            emitCancellationToken: cancellationToken);

        var results = new List<GitPreflightStepResult>(_steps.Count);
        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var stepNumber = i + 1;
            if (cancellationToken.IsCancellationRequested)
            {
                return await FinishCancelledAsync();
            }

            await EmitAsync(
                GitPreflightEventType.StepStarted,
                step.StepId,
                GitPreflightStepStatus.Running,
                step.DisplayName,
                blocking: step.CanBlock,
                stepNumber: stepNumber,
                emitCancellationToken: cancellationToken);

            var stepTimer = Stopwatch.StartNew();
            GitPreflightStepResult result;
            try
            {
                var context = new GitPreflightStepContext(
                    runId,
                    request,
                    snapshot,
                    async (message, details, ct) => await EmitAsync(
                        GitPreflightEventType.StepOutput,
                        step.StepId,
                        GitPreflightStepStatus.Running,
                        message,
                        details,
                        blocking: step.CanBlock,
                        stepNumber: stepNumber,
                        emitCancellationToken: ct),
                    results.ToList());
                result = await step.ExecuteAsync(context, cancellationToken);
                result = result with { DurationMs = stepTimer.ElapsedMilliseconds, Blocking = step.CanBlock };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FinishCancelledAsync();
            }
            catch (Exception ex)
            {
                var status = step.CanBlock
                    ? GitPreflightStepStatus.Error
                    : GitPreflightStepStatus.Warning;
                result = new GitPreflightStepResult(
                    step.StepId,
                    step.DisplayName,
                    status,
                    $"{step.DisplayName} could not complete: {ex.Message}",
                    [$"ERROR: {ex.Message}"],
                    stepTimer.ElapsedMilliseconds,
                    step.CanBlock);
            }

            results.Add(result);
            await EmitAsync(
                GitPreflightEventType.StepFinished,
                step.StepId,
                result.Status,
                result.Summary,
                result.Details,
                result.DurationMs,
                result.Blocking,
                stepNumber: stepNumber,
                emitCancellationToken: cancellationToken);
        }

        var commitAllowed = !results.Any(step =>
            step.Blocking && step.Status is GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error);
        var runStatus = !commitAllowed
            ? GitPreflightStepStatus.Blocked
            : results.Any(step => step.Status is GitPreflightStepStatus.Warning or GitPreflightStepStatus.Error)
                ? GitPreflightStepStatus.Warning
                : GitPreflightStepStatus.Passed;
        var runResult = new GitPreflightRunResult(
            runId,
            snapshot.RepositoryPath,
            snapshot.Files,
            results,
            runStatus,
            commitAllowed,
            runTimer.ElapsedMilliseconds);

        await EmitAsync(
            GitPreflightEventType.RunFinished,
            null,
            runStatus,
            commitAllowed ? "Commit allowed." : "Commit blocked.",
            durationMs: runResult.DurationMs,
            commitAllowed: commitAllowed,
            emitCancellationToken: cancellationToken);
        return runResult;

        async Task<GitPreflightRunResult> FinishCancelledAsync()
        {
            var cancelled = CreateCancelledResult(
                runId,
                snapshot.RepositoryPath,
                results,
                runTimer.ElapsedMilliseconds,
                snapshot.Files);
            await EmitAsync(
                GitPreflightEventType.RunFinished,
                null,
                GitPreflightStepStatus.Cancelled,
                "Git preflight cancelled.",
                durationMs: cancelled.DurationMs,
                commitAllowed: false,
                emitCancellationToken: CancellationToken.None);
            return cancelled;
        }
    }

    private static GitPreflightRunResult CreateCancelledResult(
        string runId,
        string repositoryPath,
        IReadOnlyList<GitPreflightStepResult> steps,
        long durationMs,
        IReadOnlyList<GitStagedFileSnapshot>? files = null) =>
        new(
            runId,
            Path.GetFullPath(repositoryPath),
            files ?? [],
            steps,
            GitPreflightStepStatus.Cancelled,
            CommitAllowed: false,
            durationMs);
}
