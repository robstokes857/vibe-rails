using System.Globalization;
using System.Text.Json;
using VibeRails.DB;
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
                    CommitMessage: null,
                    PostCommit: null));
            }

            var status = await hookService.GetStatusAsync(rootPath, cancellationToken);
            var autoInstallEnabled = IsAutoInstallEnabled(configuration)
                && !await hookService.IsAutoInstallDisabledAsync(rootPath, cancellationToken);
            var message = status.IsInstalled
                ? "VCA and Jobs Git hooks are active and current."
                : status.NeedsRepair
                    ? "Git Guard hooks are stale, disabled, or only partially installed. Repair is recommended."
                    : "VCA and Jobs Git hooks are not installed.";

            return Results.Ok(new HookStatusResponse(
                InGitRepo: true,
                IsInstalled: status.IsInstalled,
                NeedsRepair: status.NeedsRepair,
                State: status.State,
                Message: message,
                RepositoryPath: status.RepositoryPath,
                HooksPath: status.HooksPath,
                AutoInstallEnabled: autoInstallEnabled,
                PreCommit: ToResponse(status.PreCommit),
                CommitMessage: ToResponse(status.CommitMessage),
                PostCommit: ToResponse(status.PostCommit)));
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
                ? "VCA and Jobs Git hooks installed and verified"
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
                ? "VCA and Jobs Git hooks uninstalled; automatic reinstall is disabled for this repository"
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

        // POST /api/v1/git/preflight/console - Open the real hook popup for this repository.
        // The browser view above renders the same run as HTML; this exists so the console the
        // user will actually meet at commit time can be checked on demand, and so the staged
        // snapshot can be pre-checked by hand without committing to find out.
        app.MapPost("/api/v1/git/preflight/console", async (
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookActionResponse(false, "Not in a git repository"));
            }

            // A launch that could not happen is a reportable outcome, not a bad request: the page
            // shows the reason next to the button, the same way hook installation does.
            var launch = VcaHookConsoleLauncher.LaunchPreCommit(rootPath);
            return Results.Ok(new HookActionResponse(launch.Success, launch.Message));
        }).WithName("LaunchGitPreflightConsole");

        // POST /api/v1/hooks/preview - Run VCA validation over the whole working tree
        // (staged + unstaged + untracked) for the Rules page, so problems surface before
        // anything is staged. Git Guard's commit-time validation keeps using the exact
        // staged index via /api/v1/git/preflight/stream and the real hooks.
        app.MapPost("/api/v1/hooks/preview", async (
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
            try
            {
                var snapshot = await snapshotProvider.CaptureWorkingTreeAsync(rootPath, cancellationToken);
                var baseRequest = CreatePreCommitRequest(rootPath);
                var result = await validator.ExecuteAsync(
                    new GitPreflightStepContext(
                        Guid.NewGuid().ToString("N"),
                        baseRequest with
                        {
                            WorkingTreeChanges = true,
                            Invocation = baseRequest.Invocation with { WorkingTreeScope = true }
                        },
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A validator failure must reach the Rules page as raw details, not as a
                // bare 500: the card explicitly tells the user to review them.
                return Results.Ok(ToValidatorFailure("VCA validation", startedUtc, ex));
            }
        }).WithName("PreviewVcaHook");

        // POST /api/v1/code-analyzer - Analyze all current working-tree changes by itself.
        // Git Guard continues to use IGitStagedSnapshotProvider and the exact staged index.
        // ?fullScan=true widens the impact scan from tracked files to the whole directory.
        // ?scope=unpushed swaps the working-tree snapshot for an "unpushed commits" one
        //   (diff @{upstream}..HEAD) so the user can review what their local commits will do
        //   before pushing. Takes precedence over the default working-tree scope.
        app.MapPost("/api/v1/code-analyzer", async (
            bool? fullScan,
            string? scope,
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

            var unpushedScope = string.Equals(scope, "unpushed", StringComparison.OrdinalIgnoreCase);
            var startedUtc = DateTime.UtcNow;
            GitPreflightStepResult result;
            try
            {
                // The unpushed snapshot captures committed-but-not-pushed changes; the default
                // working-tree snapshot captures staged/unstaged/untracked changes. They share
                // the same downstream MintLint pipeline.
                var snapshot = unpushedScope
                    ? await snapshotProvider.CaptureUnpushedAsync(rootPath, cancellationToken)
                    : await snapshotProvider.CaptureWorkingTreeAsync(rootPath, cancellationToken);
                result = await analyzer.ExecuteAsync(
                    new GitPreflightStepContext(
                        Guid.NewGuid().ToString("N"),
                        CreatePreCommitRequest(rootPath) with
                        {
                            FullImpactScan = fullScan == true,
                            WorkingTreeChanges = true,
                            UnpushedChanges = unpushedScope
                        },
                        snapshot,
                        (_, _, _) => ValueTask.CompletedTask),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (unpushedScope)
            {
                // The most common unpushed-scope failure is "no upstream configured". Surface
                // it as a normal scan failure (not a 500) so the UI can show the message.
                return Results.Ok(ToValidatorFailure("Code analyzer", startedUtc, ex, includeValidation: false));
            }
            catch (Exception ex)
            {
                return Results.Ok(ToValidatorFailure("Code analyzer", startedUtc, ex, includeValidation: false));
            }

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
            int? ignoredFileCount = details?.TryGetValue("ignoredFileCount", out var ignoredText) == true
                && int.TryParse(ignoredText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ignoredCount)
                    ? ignoredCount
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
                Report: report,
                IgnoredFileCount: ignoredFileCount));
        }).WithName("RunCodeAnalyzer");

        // GET /api/v1/code-analyzer/source?path=... - Full working-tree file content for the
        // Code quality source pane, so a clicked metric can reveal its line inside the whole
        // file instead of an 8-line snippet. Repo-relative paths only; the same
        // symlink/junction guard a working-tree scan applies decides readability.
        app.MapGet("/api/v1/code-analyzer/source", async (
            string? path,
            string? scope,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            const long MaxSourceBytes = 5 * 1024 * 1024;
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            }

            var relativePath = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (relativePath.Length == 0 || System.IO.Path.IsPathRooted(relativePath))
            {
                return Results.BadRequest(new ErrorResponse("A repository-relative path is required."));
            }

            var repositoryRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));
            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(repositoryRoot, relativePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(repositoryRoot + System.IO.Path.DirectorySeparatorChar, comparison))
            {
                return Results.BadRequest(new ErrorResponse("The path must stay inside the repository."));
            }

            if (string.Equals(scope, "unpushed", StringComparison.OrdinalIgnoreCase))
            {
                // An unpushed scan scores the HEAD-committed revision (see F14), so serve HEAD here
                // too — otherwise the line/offset anchors from the scan wouldn't match unstaged edits
                // on disk. The working-tree guard defends against symlink/junction exfiltration and
                // doesn't apply to a blob read from the object store; containment is already enforced
                // by the repositoryRoot check above.
                var headBytes = await GitStagedSnapshotProvider.TryReadHeadBlobPublicAsync(
                    repositoryRoot, relativePath, cancellationToken);
                if (headBytes is null)
                {
                    return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, Content: null, Exists: false));
                }
                if (headBytes.Length > MaxSourceBytes)
                {
                    return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, Content: null, Truncated: true));
                }
                var headContent = GitStagedSnapshotProvider.DecodeText(headBytes, out var headBinary);
                return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, headContent, IsBinary: headBinary));
            }

            var guard = new GitStagedSnapshotProvider.WorkingTreePathGuard(repositoryRoot);
            if (!guard.IsReadableRegularFile(fullPath))
            {
                return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, Content: null, Exists: false));
            }

            if (new FileInfo(fullPath).Length > MaxSourceBytes)
            {
                return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, Content: null, Truncated: true));
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            var content = GitStagedSnapshotProvider.DecodeText(bytes, out var isBinary);
            return Results.Ok(new CodeAnalyzerSourceResponse(relativePath, content, IsBinary: isBinary));
        }).WithName("GetCodeAnalyzerSource");

        // Code quality ignore list — files the user removed from scan results, persisted
        // per repository in state.db. The MintLint step drops them before analysis.
        app.MapGet("/api/v1/code-analyzer/ignores", async (
            ICodeAnalyzerIgnoreStore ignoreStore,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            }

            var files = await ignoreStore.ListAsync(rootPath, cancellationToken);
            return Results.Ok(new CodeAnalyzerIgnoreListResponse(
                [.. files.Select(file => new CodeAnalyzerIgnoreEntryResponse(
                    file.Path, file.MatchKind, file.ReasonKind, file.ReasonText, file.CreatedUtc))]));
        }).WithName("ListCodeAnalyzerIgnores");

        app.MapPost("/api/v1/code-analyzer/ignores", async (
            CodeAnalyzerIgnoreRequest request,
            ICodeAnalyzerIgnoreStore ignoreStore,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            }

            var path = CodeAnalyzerIgnoreStore.NormalizePath(request.Path ?? string.Empty);
            if (path.Length == 0 || System.IO.Path.IsPathRooted(path) || HasParentTraversal(path))
            {
                return Results.BadRequest(new ErrorResponse("A repository-relative path is required."));
            }

            var matchKind = NormalizeMatchKind(request.MatchKind);
            var reasonKind = NormalizeReasonKind(request.ReasonKind);
            var reasonText = string.IsNullOrWhiteSpace(request.ReasonText) ? null : request.ReasonText.Trim();
            await ignoreStore.UpsertAsync(
                rootPath,
                new CodeAnalyzerIgnoredFile(path, matchKind, reasonKind, reasonText, DateTime.UtcNow),
                cancellationToken);
            var label = matchKind == CodeAnalyzerIgnoreMatchKind.Directory ? "directory" : "file";
            return Results.Ok(new HookActionResponse(true, $"{path} ({label}) is now ignored by Code quality scans."));
        }).WithName("AddCodeAnalyzerIgnore");

        // Multi-file ignore with a shared reason — one request per bulk selection in the UI,
        // so the user doesn't watch N toasts fire as each round-trip completes. MatchKind is
        // shared across all paths (you typically select "ignore these files" OR "ignore these
        // directories", not a mix). Empty/duplicate paths are silently skipped.
        app.MapPost("/api/v1/code-analyzer/ignores/bulk", async (
            CodeAnalyzerIgnoreBulkRequest request,
            ICodeAnalyzerIgnoreStore ignoreStore,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            }

            const int MaxBulkPaths = 500;
            var requestedCount = request.Paths?.Count ?? 0;
            var paths = (request.Paths ?? [])
                .Select(p => CodeAnalyzerIgnoreStore.NormalizePath(p ?? string.Empty))
                .Where(p => p.Length > 0 && !System.IO.Path.IsPathRooted(p) && !HasParentTraversal(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                return Results.BadRequest(new ErrorResponse("At least one repository-relative path is required."));
            }
            if (paths.Count > MaxBulkPaths)
            {
                return Results.BadRequest(new ErrorResponse($"Too many paths in one request (max {MaxBulkPaths})."));
            }

            var matchKind = NormalizeMatchKind(request.MatchKind);
            var reasonKind = NormalizeReasonKind(request.ReasonKind);
            var reasonText = string.IsNullOrWhiteSpace(request.ReasonText) ? null : request.ReasonText.Trim();
            var createdUtc = DateTime.UtcNow;
            var files = paths
                .Select(path => new CodeAnalyzerIgnoredFile(path, matchKind, reasonKind, reasonText, createdUtc))
                .ToList();
            await ignoreStore.UpsertManyAsync(rootPath, files, cancellationToken);

            // Empty, absolute, "..", and duplicate entries were dropped above — report them honestly
            // instead of the previous hardcoded 0.
            var skipped = Math.Max(0, requestedCount - paths.Count);
            var label = matchKind == CodeAnalyzerIgnoreMatchKind.Directory ? "directories" : "files";
            return Results.Ok(new CodeAnalyzerIgnoreBulkResponse(
                paths.Count,
                skipped,
                $"Ignored {paths.Count} {label}."));
        }).WithName("BulkAddCodeAnalyzerIgnores");

        app.MapDelete("/api/v1/code-analyzer/ignores", async (
            string? path,
            ICodeAnalyzerIgnoreStore ignoreStore,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            }

            var normalized = CodeAnalyzerIgnoreStore.NormalizePath(path ?? string.Empty);
            if (normalized.Length == 0)
            {
                return Results.BadRequest(new ErrorResponse("A repository-relative path is required."));
            }

            var removed = await ignoreStore.RemoveAsync(rootPath, normalized, cancellationToken);
            return Results.Ok(new HookActionResponse(
                removed,
                removed
                    ? $"{normalized} will be scanned again."
                    : $"{normalized} was not on the ignore list."));
        }).WithName("RemoveCodeAnalyzerIgnore");

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

    /// <summary>
    /// Shapes an exception from a preview/analyzer run as a normal error-status response,
    /// so the Rules page renders its "the validator itself failed" card with the actual
    /// failure text instead of receiving an empty 500.
    /// </summary>
    private static HookPreviewResponse ToValidatorFailure(
        string title,
        DateTime startedUtc,
        Exception exception,
        bool includeValidation = true) =>
        new(
            Success: false,
            ExitCode: 1,
            Status: "error",
            Title: title,
            Output: $"[error] {exception.Message}",
            StartedUtc: startedUtc,
            DurationMs: (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
            Validation: includeValidation
                ? BuildVcaValidationOverview(new VcaHookValidationSummary(
                    HasError: true,
                    HasStopViolation: false,
                    HasCommitViolations: false,
                    RequiredAcknowledgments: []))
                : null);

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

    // Repo-relative paths must not escape the repository via parent-directory segments.
    // NormalizePath already forward-slashes and strips a leading "./", so a ".." as a whole path
    // segment is a traversal attempt.
    private static bool HasParentTraversal(string normalizedPath) =>
        normalizedPath == ".."
        || normalizedPath.StartsWith("../", StringComparison.Ordinal)
        || normalizedPath.EndsWith("/..", StringComparison.Ordinal)
        || normalizedPath.Contains("/../", StringComparison.Ordinal);

    // Coerces incoming MatchKind (file/directory) to a canonical lowercase value,
    // defaulting to "file" for null/empty/unknown so the column invariant holds.
    private static string NormalizeMatchKind(string? matchKind)
    {
        if (string.IsNullOrWhiteSpace(matchKind)) return CodeAnalyzerIgnoreMatchKind.File;
        var lower = matchKind.Trim().ToLowerInvariant();
        return lower is CodeAnalyzerIgnoreMatchKind.File or CodeAnalyzerIgnoreMatchKind.Directory
            ? lower
            : CodeAnalyzerIgnoreMatchKind.File;
    }

    // Validates reason kinds against the fixed set the UI offers (test/config/other).
    // Returns null for "no reason given" so the column stays NULL, matching the schema.
    private static string? NormalizeReasonKind(string? reasonKind)
    {
        if (string.IsNullOrWhiteSpace(reasonKind)) return null;
        var lower = reasonKind.Trim().ToLowerInvariant();
        return lower is "test" or "config" or "other" ? lower : null;
    }
}
