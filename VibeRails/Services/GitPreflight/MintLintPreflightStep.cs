using System.Globalization;
using MintLint;
using Serilog;
using VibeRails.DB;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public sealed class MintLintPreflightStep(ICodeAnalyzerIgnoreStore? ignoreStore = null) : IGitPreflightStep
{
    public const string Id = "mintlint";

    public string StepId => Id;
    public string DisplayName => "MintLint code health";
    public bool CanBlock => false;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.Invocation.Kind is VcaHookKind.CommitMessage
            or VcaHookKind.AcknowledgeCommitMessage)
        {
            const string message = "MintLint already ran during pre-commit; commit-message scan skipped.";
            await context.WriteOutputAsync(message, null, cancellationToken);
            return new GitPreflightStepResult(
                Id,
                DisplayName,
                GitPreflightStepStatus.Skipped,
                message,
                [message],
                DurationMs: 0,
                Blocking: false);
        }

        // The user's ignore list removes files from Code quality results entirely — both
        // here and in the Rules-page scan, which share this step. Never let a store failure
        // break the (non-blocking) analysis; a missing list just means nothing is ignored.
        IReadOnlyList<CodeAnalyzerIgnoredFile> ignoreRules = [];
        if (ignoreStore is not null)
        {
            try
            {
                ignoreRules = await ignoreStore.GetIgnoreRulesAsync(
                    context.Snapshot.RepositoryPath,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Warning(exception, "[MintLint] Could not load the Code quality ignore list; scanning everything.");
            }
        }

        var supportedFiles = context.Snapshot.Files
            .Where(file => file.ExistsInIndex
                && !file.IsBinary
                && file.Content != null
                && MintLintAnalyzer.SupportsFile(file.RelativePath))
            .ToList();
        var ignoredCount = supportedFiles.Count(file =>
            CodeAnalyzerIgnoreStore.IsIgnored(file.RelativePath, ignoreRules));
        var eligibleFiles = supportedFiles
            .Where(file => !CodeAnalyzerIgnoreStore.IsIgnored(file.RelativePath, ignoreRules))
            .ToList();
        // Production snapshots carry a zero-context Git patch. Null remains a deliberate
        // fallback for preview/test snapshots created before AddedContent was introduced.
        var candidates = eligibleFiles
            .Select(file => new MintLintChangeCandidate(
                file,
                file.AddedContent ?? file.Content!))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.AddedContent))
            .ToList();
        var noAddedCodeCount = eligibleFiles.Count - candidates.Count;
        // "Skipped" means every snapshot file that was not analyzed and was not explicitly
        // ignored: unsupported/deleted/binary files plus source changes containing no additions.
        var skippedCount = context.Snapshot.Files.Count - candidates.Count - ignoredCount;
        // The scope label distinguishes the three scan modes in step output: "staged" for
        // commit preflight, "changed" for the working-tree Rules scan, "unpushed" for the
        // committed-but-not-pushed scan. Same pipeline, different framing for the user.
        var sourceScope = context.Request.UnpushedChanges
            ? "unpushed"
            : context.Request.WorkingTreeChanges
                ? "changed"
                : "staged";

        if (candidates.Count == 0)
        {
            var ignoredSuffix = ignoredCount > 0 ? $", {ignoredCount} ignored" : "";
            var noAdditionsSuffix = noAddedCodeCount > 0
                ? $", {noAddedCodeCount} with no added lines"
                : "";
            var skipped =
                $"No added code in supported {sourceScope} source files to scan "
                + $"({skippedCount} skipped{noAdditionsSuffix}{ignoredSuffix}).";
            var emptyDetails = new Dictionary<string, string>
            {
                ["supportedFileCount"] = "0",
                ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture),
                ["ignoredFileCount"] = ignoredCount.ToString(CultureInfo.InvariantCulture),
                ["noAddedCodeFileCount"] = noAddedCodeCount.ToString(CultureInfo.InvariantCulture)
            };
            await context.WriteOutputAsync(skipped, emptyDetails, cancellationToken);
            return new GitPreflightStepResult(
                Id,
                DisplayName,
                GitPreflightStepStatus.Skipped,
                skipped,
                [skipped],
                DurationMs: 0,
                Blocking: false,
                new Dictionary<string, string>(emptyDetails));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var changedInputs = candidates
            .Select(candidate => new SourceInput(
                candidate.File.RelativePath,
                candidate.AddedContent))
            .ToList();
        var analyzed = MintLintAnalyzer.AnalyzeSources(changedInputs);
        var scan = MintLintScorer.Score(analyzed);

        // Reach across the repository: bad code that many files reference outranks bad
        // code nothing uses. Discover declared names from each complete current file, while
        // retaining change-only metrics for the score itself. Tracked files only by default;
        // a full-repo scan also walks untracked source. Failure here must never block the report.
        IReadOnlyDictionary<string, int> referencedBy;
        try
        {
            var impactTargets = MintLintAnalyzer.AnalyzeSources(candidates
                .Select(candidate => new SourceInput(
                    candidate.File.RelativePath,
                    candidate.File.Content!))
                .ToList());
            referencedBy = context.Request.UnpushedChanges && context.Snapshot.ImpactFiles is not null
                ? ImpactAnalyzer.CountReferencingSources(
                    impactTargets,
                    context.Snapshot.ImpactFiles
                        .Select(file => new SourceInput(file.RelativePath, file.Content))
                        .ToList())
                : ImpactAnalyzer.CountReferencingFiles(
                    context.Snapshot.RepositoryPath,
                    impactTargets,
                    context.Request.FullImpactScan ? null : context.Snapshot.TrackedFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            referencedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var contentByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceLineNumbersByFile =
            new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var displayPath = candidate.File.RelativePath.Replace('\\', '/');
            while (displayPath.StartsWith("./", StringComparison.Ordinal))
            {
                displayPath = displayPath[2..];
            }

            contentByFile[displayPath] = candidate.File.Content!;
            if (candidate.File.AddedLineNumbers is { } sourceLines)
            {
                sourceLineNumbersByFile[displayPath] = sourceLines;
            }
        }

        var output = new List<string>
        {
            ignoredCount > 0
                ? $"Scanned added code in {scan.Files.Count} supported {sourceScope} source file(s); skipped {skippedCount}, ignored {ignoredCount} by user preference."
                : $"Scanned added code in {scan.Files.Count} supported {sourceScope} source file(s); skipped {skippedCount}.",
            $"Overall risk: {scan.Overall.Score:0.0}/100 · {scan.Overall.Rating}"
        };

        foreach (var file in scan.Files.Take(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concerns = file.Categories
                .Where(category => category.Score > 0)
                .OrderByDescending(category => category.Score)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .Take(2)
                .Select(category => $"{category.Name} {category.Score:0.0}")
                .ToList();
            var concernText = concerns.Count == 0 ? "no notable concerns" : string.Join(", ", concerns);
            output.Add($"{file.File}: {file.Overall.Score:0.0} {file.Overall.Rating} · {concernText}");
        }

        foreach (var line in output)
        {
            await context.WriteOutputAsync(line, null, cancellationToken);
        }

        var warning = scan.Overall.Rating is "NeedsWork" or "AtRisk"
            || scan.Files.Any(file => file.Overall.Rating is "NeedsWork" or "AtRisk");
        var report = MintLintReportFactory.Build(
            scan,
            skippedCount,
            contentByFile,
            referencedBy,
            sourceLineNumbersByFile: sourceLineNumbersByFile);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["supportedFileCount"] = scan.Files.Count.ToString(CultureInfo.InvariantCulture),
            ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture),
            ["ignoredFileCount"] = ignoredCount.ToString(CultureInfo.InvariantCulture),
            ["noAddedCodeFileCount"] = noAddedCodeCount.ToString(CultureInfo.InvariantCulture),
            ["overallScore"] = scan.Overall.Score.ToString("0.0", CultureInfo.InvariantCulture),
            ["overallRating"] = scan.Overall.Rating,
            ["worstFiles"] = string.Join("\n", output.Skip(2)),
            [MintLintReportFactory.DetailsKey] = MintLintReportFactory.ToJson(report)
        };

        return new GitPreflightStepResult(
            Id,
            DisplayName,
            warning ? GitPreflightStepStatus.Warning : GitPreflightStepStatus.Passed,
            warning
                ? $"MintLint found code-health concerns ({scan.Overall.Rating}); commit remains allowed."
                : $"MintLint finished added-code analysis with a {scan.Overall.Rating} rating.",
            output,
            DurationMs: 0,
            Blocking: false,
            details);
    }

    private sealed record MintLintChangeCandidate(
        GitStagedFileSnapshot File,
        string AddedContent);
}
