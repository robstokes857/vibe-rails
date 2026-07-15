using System.Globalization;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Routes;

public static class HookRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/hooks/status - Check if VCA git hooks are installed
        app.MapGet("/api/v1/hooks/status", async (
            IHookInstallationService hookService,
            IGitService gitService,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.Ok(new HookStatusResponse(
                    InGitRepo: false,
                    IsInstalled: false,
                    NeedsRepair: false,
                    State: "unavailable",
                    Message: "Not in a git repository",
                    RepositoryPath: null,
                    HooksPath: null,
                    AutoInstallEnabled: IsAutoInstallEnabled(configuration),
                    PreCommit: null,
                    CommitMessage: null));
            }

            var status = await hookService.GetStatusAsync(rootPath, cancellationToken);
            var message = status.IsInstalled
                ? "Both VCA hooks are active and current."
                : status.NeedsRepair
                    ? "VCA hooks are stale, disabled, or only partially installed. Repair is recommended."
                    : "VCA hooks are not installed.";

            return Results.Ok(new HookStatusResponse(
                InGitRepo: true,
                IsInstalled: status.IsInstalled,
                NeedsRepair: status.NeedsRepair,
                State: status.State,
                Message: message,
                RepositoryPath: status.RepositoryPath,
                HooksPath: status.HooksPath,
                AutoInstallEnabled: IsAutoInstallEnabled(configuration),
                PreCommit: ToResponse(status.PreCommit),
                CommitMessage: ToResponse(status.CommitMessage)));
        }).WithName("GetHookStatus");

        // POST /api/v1/hooks/install - Install the VCA git hooks
        app.MapPost("/api/v1/hooks/install", async (
            IHookInstallationService hookService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookActionResponse(false, "Not in a git repository"));
            }

            var result = await hookService.InstallHooksAsync(rootPath, cancellationToken);
            var verified = result.Success
                ? await hookService.GetStatusAsync(rootPath, cancellationToken)
                : null;
            var success = result.Success && verified?.IsInstalled == true;
            var message = success
                ? "VCA git hooks installed and verified"
                : $"{result.ErrorMessage} {(result.Details != null ? $"({result.Details})" : "")}";
            if (result.Success && !success)
            {
                message = "Hook files were written, but integrity verification did not pass.";
            }

            return Results.Ok(new HookActionResponse(success, message.Trim()));
        }).WithName("InstallHook");

        // DELETE /api/v1/hooks - Uninstall the VCA git hooks
        app.MapDelete("/api/v1/hooks", async (
            IHookInstallationService hookService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookActionResponse(false, "Not in a git repository"));
            }

            var result = await hookService.UninstallHooksAsync(rootPath, cancellationToken);
            var message = result.Success
                ? "VCA git hooks uninstalled"
                : $"{result.ErrorMessage} {(result.Details != null ? $"({result.Details})" : "")}";
            return Results.Ok(new HookActionResponse(result.Success, message));
        }).WithName("UninstallHook");

        // POST /api/v1/git/preflight/stream - Stream one staged-index preflight run.
        // Fetch streaming is used instead of EventSource because this is an authenticated
        // POST and every request must carry the per-tab security header.
        app.MapPost("/api/v1/git/preflight/stream", async (
            HttpContext context,
            IGitPreflightPipeline pipeline,
            IGitService gitService) =>
        {
            var cancellationToken = context.RequestAborted;
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                var errorJson = JsonSerializer.Serialize(
                    new ErrorResponse("Not in a git repository"),
                    AppJsonSerializerContext.Default.ErrorResponse);
                await context.Response.WriteAsync(errorJson, cancellationToken);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Append("X-Accel-Buffering", "no");
            await context.Response.StartAsync(cancellationToken);

            string? activeRunId = null;
            long lastSequence = 0;

            async ValueTask WriteEventAsync(GitPreflightEvent preflightEvent, CancellationToken token)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }

                activeRunId = preflightEvent.RunId;
                lastSequence = preflightEvent.Sequence;
                var response = ToResponse(preflightEvent);
                var json = JsonSerializer.Serialize(
                    response,
                    AppJsonSerializerContext.Default.GitPreflightEventResponse);
                try
                {
                    await context.Response.WriteAsync($"data: {json}\n\n", token);
                    await context.Response.Body.FlushAsync(token);
                }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // The browser intentionally closed the stream (Cancel/navigation).
                }
            }

            try
            {
                await pipeline.RunAsync(
                    CreatePreCommitRequest(rootPath),
                    WriteEventAsync,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Browser-side Cancel aborts the request, which is the cancellation signal
                // for both the Git processes and the active preflight step.
            }
            catch (Exception ex)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }

                var errorEvent = new GitPreflightEvent(
                    activeRunId ?? Guid.NewGuid().ToString("N"),
                    lastSequence + 1,
                    DateTimeOffset.UtcNow,
                    GitPreflightEventType.RunFinished,
                    StepId: null,
                    GitPreflightStepStatus.Error,
                    $"Git preflight failed: {ex.Message}",
                    DurationMs: null,
                    Blocking: true,
                    CommitAllowed: false);
                try
                {
                    await WriteEventAsync(errorEvent, CancellationToken.None);
                }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // Nothing remains connected to receive the terminal error event.
                }
            }
        }).WithName("StreamGitPreflight");

        // POST /api/v1/hooks/preview - Run staged VCA validation by itself for the Rules page.
        // The full Git Guard pipeline remains available through /api/v1/git/preflight/stream.
        app.MapPost("/api/v1/hooks/preview", async (
            IGitStagedSnapshotProvider snapshotProvider,
            IEnumerable<IGitPreflightStep> preflightSteps,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookPreviewResponse(
                    Success: false,
                    ExitCode: 1,
                    Status: "error",
                    Title: "Pre-commit hook check",
                    Output: "[error] Not in a git repository.",
                    StartedUtc: DateTime.UtcNow,
                    DurationMs: 0,
                    Validation: BuildVcaValidationOverview(new VcaHookValidationSummary(
                        HasError: true,
                        HasStopViolation: false,
                        HasCommitViolations: false,
                        RequiredAcknowledgments: []))));
            }

            var validator = preflightSteps.FirstOrDefault(step => step.StepId == VcaPreflightStep.Id);
            if (validator is null)
            {
                return Results.Problem("VCA validation is unavailable.");
            }

            var startedUtc = DateTime.UtcNow;
            var snapshot = await snapshotProvider.CaptureAsync(rootPath, cancellationToken);
            var result = await validator.ExecuteAsync(
                new GitPreflightStepContext(
                    Guid.NewGuid().ToString("N"),
                    CreatePreCommitRequest(rootPath),
                    snapshot,
                    (_, _, _) => ValueTask.CompletedTask),
                cancellationToken);
            var succeeded = result.Status is not GitPreflightStepStatus.Blocked
                and not GitPreflightStepStatus.Error
                and not GitPreflightStepStatus.Cancelled;
            var output = string.Join(Environment.NewLine, result.Output);
            var validationSummary = result.VcaSummary ?? new VcaHookValidationSummary(
                HasError: result.Status is GitPreflightStepStatus.Error or GitPreflightStepStatus.Cancelled,
                HasStopViolation: result.Status == GitPreflightStepStatus.Blocked,
                HasCommitViolations: result.Status == GitPreflightStepStatus.Warning,
                RequiredAcknowledgments: [],
                StagedFileCount: snapshot.Files.Count);

            return Results.Ok(new HookPreviewResponse(
                Success: succeeded,
                ExitCode: succeeded ? 0 : 1,
                Status: ToWireStatus(result.Status),
                Title: "VCA validation",
                Output: output,
                StartedUtc: startedUtc,
                DurationMs: (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
                Validation: BuildVcaValidationOverview(validationSummary)));
        }).WithName("PreviewVcaHook");

        // POST /api/v1/code-analyzer - Analyze all current working-tree changes by itself.
        // Git Guard continues to use IGitStagedSnapshotProvider and the exact staged index.
        // ?fullScan=true widens the impact scan from tracked files to the whole directory.
        app.MapPost("/api/v1/code-analyzer", async (
            bool? fullScan,
            IGitWorkingTreeSnapshotProvider snapshotProvider,
            IEnumerable<IGitPreflightStep> preflightSteps,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookPreviewResponse(
                    Success: false,
                    ExitCode: 1,
                    Status: "error",
                    Title: "Code analyzer",
                    Output: "[error] Not in a git repository.",
                    StartedUtc: DateTime.UtcNow,
                    DurationMs: 0));
            }

            var analyzer = preflightSteps.FirstOrDefault(step => step.StepId == MintLintPreflightStep.Id);
            if (analyzer is null)
            {
                return Results.Problem("The code analyzer is unavailable.");
            }

            var startedUtc = DateTime.UtcNow;
            var snapshot = await snapshotProvider.CaptureWorkingTreeAsync(rootPath, cancellationToken);
            var result = await analyzer.ExecuteAsync(
                new GitPreflightStepContext(
                    Guid.NewGuid().ToString("N"),
                    CreatePreCommitRequest(rootPath) with
                    {
                        FullImpactScan = fullScan == true,
                        WorkingTreeChanges = true
                    },
                    snapshot,
                    (_, _, _) => ValueTask.CompletedTask),
                cancellationToken);
            var succeeded = result.Status is not GitPreflightStepStatus.Error
                and not GitPreflightStepStatus.Cancelled;
            var output = string.Join(Environment.NewLine, result.Output);
            var details = result.Details;
            double? healthScore = details?.TryGetValue("overallScore", out var concernText) == true
                && double.TryParse(concernText, NumberStyles.Float, CultureInfo.InvariantCulture, out var concernScore)
                    ? Math.Round(Math.Clamp(100 - concernScore, 0, 100), 1)
                    : null;
            int? analyzedFileCount = details?.TryGetValue("supportedFileCount", out var analyzedText) == true
                && int.TryParse(analyzedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var analyzedCount)
                    ? analyzedCount
                    : null;
            int? skippedFileCount = details?.TryGetValue("skippedFileCount", out var skippedText) == true
                && int.TryParse(skippedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var skippedCount)
                    ? skippedCount
                    : null;
            var rating = details?.GetValueOrDefault("overallRating");
            var report = MintLintReportFactory.FromJson(details?.GetValueOrDefault(MintLintReportFactory.DetailsKey));

            return Results.Ok(new HookPreviewResponse(
                Success: succeeded,
                ExitCode: succeeded ? 0 : 1,
                Status: ToWireStatus(result.Status),
                Title: "Code analyzer",
                Output: output,
                StartedUtc: startedUtc,
                DurationMs: (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
                HealthScore: healthScore,
                Rating: rating,
                AnalyzedFileCount: analyzedFileCount,
                SkippedFileCount: skippedFileCount,
                Report: report));
        }).WithName("RunCodeAnalyzer");

        // POST /api/v1/hooks/validate - Run VCA validation manually
        app.MapPost("/api/v1/hooks/validate", async (
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ValidationResponse(false, "Not in a git repository", new List<ValidationResultResponse>()));
            }

            using var transcript = new StringWriter();
            var exitCode = await VcaHookProcessHost.RunAsync(
                ["--vca-hook", "pre-commit", "--workdir", rootPath],
                transcript,
                transcript,
                cancellationToken: cancellationToken);
            var output = transcript.ToString().TrimEnd();

            return Results.Ok(new ValidationResponse(
                Passed: exitCode == 0,
                Message: exitCode == 0 ? "VCA hook check passed" : "VCA hook check blocked the commit",
                Results:
                [
                    new ValidationResultResponse(
                        RuleName: "VCA pre-commit pipeline",
                        Enforcement: exitCode == 0 ? "WARN" : "STOP",
                        Passed: exitCode == 0,
                        Message: output,
                        AffectedFiles: null)
                ]));
        }).WithName("ValidateVca");
    }

    private static HookFileStatusResponse ToResponse(GitHookFileStatus status) =>
        new(
            status.Name,
            status.State.ToString().ToLowerInvariant(),
            status.HasVibeRailsSection,
            status.IsCurrent,
            status.Message);

    private static GitPreflightRequest CreatePreCommitRequest(string rootPath) =>
        new(
            rootPath,
            new VcaHookInvocation(
                VcaHookKind.PreCommit,
                CommitMessagePath: null,
                WorkingDirectory: rootPath,
                DemoUi: false,
                DemoDuration: TimeSpan.Zero,
                PromptForAcknowledgment: false,
                ShowConsoleWindow: false));

    private static GitPreflightEventResponse ToResponse(GitPreflightEvent preflightEvent) =>
        new(
            preflightEvent.RunId,
            preflightEvent.Sequence,
            preflightEvent.TimestampUtc,
            ToWireType(preflightEvent.Type),
            preflightEvent.StepId,
            ToWireStatus(preflightEvent.Status),
            preflightEvent.Message,
            preflightEvent.Type == GitPreflightEventType.StepOutput
                ? preflightEvent.Message
                : null,
            preflightEvent.Details is null
                ? null
                : new Dictionary<string, string>(preflightEvent.Details, StringComparer.Ordinal),
            preflightEvent.DurationMs,
            preflightEvent.Blocking,
            preflightEvent.CommitAllowed,
            preflightEvent.StepNumber,
            preflightEvent.StepCount);

    internal static VcaValidationOverviewResponse BuildVcaValidationOverview(
        VcaHookValidationSummary summary)
    {
        var findings = (summary.Findings ?? [])
            .Select(finding => new VcaRuleFindingResponse(
                ToWireFindingKind(finding.Kind),
                finding.Enforcement,
                finding.Rule,
                finding.Reason,
                finding.SourcePath,
                finding.Guidance,
                finding.Acknowledgment))
            .ToList();
        var stopCount = findings.Count(finding => finding.Status == "blocked");
        var commitCount = findings.Count(finding => finding.Status == "acknowledgment_required");
        var warningCount = findings.Count(finding => finding.Status == "warning");
        var deferredCount = findings.Count(finding => finding.Status == "deferred");
        var outcome = summary.HasError
            ? "error"
            : summary.HasStopViolation || stopCount > 0
                ? "blocked"
                : summary.HasCommitViolations || commitCount > 0 || warningCount > 0
                    ? "attention"
                    : summary.StagedFileCount == 0
                        ? "empty"
                        : "passed";

        return new VcaValidationOverviewResponse(
            outcome,
            summary.StagedFileCount,
            summary.ApplicableRuleCount,
            findings.Count,
            stopCount,
            commitCount,
            warningCount,
            deferredCount,
            findings);
    }

    private static string ToWireFindingKind(VcaRuleFindingKind kind) => kind switch
    {
        VcaRuleFindingKind.Warning => "warning",
        VcaRuleFindingKind.Deferred => "deferred",
        VcaRuleFindingKind.AcknowledgmentRequired => "acknowledgment_required",
        VcaRuleFindingKind.Blocked => "blocked",
        _ => "warning"
    };

    private static string ToWireType(GitPreflightEventType type) => type switch
    {
        GitPreflightEventType.RunStarted => "run_started",
        GitPreflightEventType.StepStarted => "step_started",
        GitPreflightEventType.StepOutput => "step_output",
        GitPreflightEventType.StepFinished => "step_finished",
        GitPreflightEventType.RunFinished => "run_finished",
        _ => "unknown"
    };

    private static string ToWireStatus(GitPreflightStepStatus status) => status switch
    {
        GitPreflightStepStatus.Running => "running",
        GitPreflightStepStatus.Passed => "passed",
        GitPreflightStepStatus.Warning => "warning",
        GitPreflightStepStatus.Blocked => "blocked",
        GitPreflightStepStatus.Skipped => "skipped",
        GitPreflightStepStatus.Error => "error",
        GitPreflightStepStatus.Cancelled => "cancelled",
        _ => "error"
    };

    private static bool IsAutoInstallEnabled(IConfiguration configuration)
    {
        var hooksSection = configuration.GetSection("VibeRails:Hooks");
        return hooksSection.GetValue("AutoInstall", true)
            && hooksSection.GetValue("InstallOnStartup", true);
    }
}
