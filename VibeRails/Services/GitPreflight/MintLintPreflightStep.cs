using System.Globalization;
using MintLint;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public sealed class MintLintPreflightStep(ICodeAnalyzerIgnoreStore? ignoreStore = null,
    IReadOnlyList<CodeAnalyzerIgnoredFile>? capturedIgnoreRules = null) : IGitPreflightStep
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
        IReadOnlyList<CodeAnalyzerIgnoredFile> ignoreRules = capturedIgnoreRules ?? [];
        if (capturedIgnoreRules is null && ignoreStore is not null)
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

        ignoreRules = [.. ignoreRules, .. context.Snapshot.QualityExemptions ?? []];

        var supportedFiles = context.Snapshot.Files
            .Where(file => file.ExistsInIndex
                && !file.IsBinary
                && file.Content != null
                && MintLintAnalyzer.SupportsFile(file.RelativePath))
            .ToList();
        var ignoredCount = supportedFiles.Count(file =>
            CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, ignoreRules));
        var eligibleFiles = supportedFiles
            .Where(file => !CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, ignoreRules))
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
        var sourceScope = context.Request.ScopeLabel ?? (context.Request.UnpushedChanges
            ? "unpushed"
            : context.Request.WorkingTreeChanges
                ? "changed"
                : "staged");

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
        // Added lines are graded for size, dependencies and duplication; a function the change
        // created or made worse is graded whole, at its real line (see ChangeAnalyzer).
        var analysis = ChangeAnalyzer.Analyze(candidates.Select(candidate => ChangedCode.ToChange(candidate.File)).ToList());
        var scan = MintLintScorer.Score(analysis.Files);
        var health = string.Create(CultureInfo.InvariantCulture, $"{scan.Overall.Health:0.0}");

        var scanned = ignoredCount > 0
            ? $"Scanned added code in {scan.Files.Count} supported {sourceScope} source file(s); skipped {skippedCount}, ignored {ignoredCount} by user preference."
            : $"Scanned added code in {scan.Files.Count} supported {sourceScope} source file(s); skipped {skippedCount}.";
        var (output, needWork) = Describe(scanned, scan, analysis);
        cancellationToken.ThrowIfCancellationRequested();
        var worstFiles = WorstFiles(scan);

        foreach (var line in output)
        {
            await context.WriteOutputAsync(line, null, cancellationToken);
        }

        var warning = scan.Overall.Rating is "NeedsWork" or "AtRisk"
            || scan.Files.Any(file => file.Overall.Rating is "NeedsWork" or "AtRisk");
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["supportedFileCount"] = scan.Files.Count.ToString(CultureInfo.InvariantCulture),
            ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture),
            ["ignoredFileCount"] = ignoredCount.ToString(CultureInfo.InvariantCulture),
            ["noAddedCodeFileCount"] = noAddedCodeCount.ToString(CultureInfo.InvariantCulture),
            ["overallScore"] = scan.Overall.Score.ToString("0.0", CultureInfo.InvariantCulture),
            ["overallRating"] = scan.Overall.Rating,
            ["overallGrade"] = scan.Overall.Grade.ToString(),
            ["worstFiles"] = string.Join("\n", worstFiles)
        };
        if (context.Request.IncludeReport)
        {
            details[MintLintReportFactory.DetailsKey] = MintLintReportFactory.ToJson(
                BuildReport(context, candidates, analysis, (scan, skippedCount)));
        }

        return new GitPreflightStepResult(
            Id,
            DisplayName,
            warning ? GitPreflightStepStatus.Warning : GitPreflightStepStatus.Passed,
            warning
                ? $"MintLint graded the added code {scan.Overall.Grade} (health {health}/100); {needWork} file(s) need work. The commit remains allowed."
                : $"MintLint graded the added code {scan.Overall.Grade} (health {health}/100).",
            output,
            DurationMs: 0,
            Blocking: false,
            details);
    }

    // Git Guard's console shows the worst few; the saved report keeps every file and finding.
    private const int OutputFiles = 5, OutputProblems = 4;

    /// <summary>
    /// The lines Git Guard prints: what was scanned, the overall grade, then what to fix in the words
    /// the lane check and the Code quality rule use: each file below C with its problems, where they
    /// are and how to fix them. Also returns how many files grade below C.
    /// </summary>
    private static (List<string> Output, int NeedWork) Describe(string scanned, ScanResult scan, ChangeAnalysis analysis)
    {
        var health = string.Create(CultureInfo.InvariantCulture, $"{scan.Overall.Health:0.0}");
        var graded = CodeQualityFindings.FromReport(MintLintReportFactory.Build(scan, 0, functionBaselines: analysis.Baselines));
        var needWork = CodeQualityFixList.ToFix(graded, null).Count;
        List<string> output =
        [
            scanned,
            $"Overall grade {scan.Overall.Grade} · health {health}/100, weighted by added lines.",
            needWork == 0 ? "No file grades below C." : $"{needWork} file(s) grade below C. Fix these, worst first:",
            .. CodeQualityFixList.FixLines(graded, null, OutputFiles, OutputProblems)
        ];
        return (output, needWork);
    }

    /// <summary>The older machine-readable form (path, concern, rating, top categories), kept for
    /// readers of a result that has no report.</summary>
    private static List<string> WorstFiles(ScanResult scan)
    {
        var worstFiles = new List<string>();
        foreach (var file in scan.Files.Take(5))
        {
            var concerns = file.Categories
                .Where(category => category.Score > 0)
                .OrderByDescending(category => category.Score)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .Take(2)
                .Select(category => $"{category.Name} {category.Score:0.0}")
                .ToList();
            var concernText = concerns.Count == 0 ? "no notable concerns" : string.Join(", ", concerns);
            worstFiles.Add($"{file.File}: {file.Overall.Score:0.0} {file.Overall.Rating} · {concernText}");
        }
        return worstFiles;
    }

    private static MintLintReportResponse BuildReport(
        GitPreflightStepContext context,
        IReadOnlyList<MintLintChangeCandidate> candidates,
        ChangeAnalysis analysis,
        (ScanResult Scan, int SkippedCount) scanned)
    {
        var (scan, skippedCount) = scanned;
        // Reach across the repository: bad code that many files reference outranks bad
        // code nothing uses. Declared names come from each complete current file, which the
        // change analysis already measured; the score itself stays change-only. Tracked files
        // only by default; a full-repo scan adds untracked source. Failure here must never
        // block the report.
        IReadOnlyDictionary<string, int> referencedBy;
        try
        {
            var impactTargets = analysis.WholeFiles;
            referencedBy = context.Snapshot.ImpactFiles is not null
                ? ImpactAnalyzer.CountReferencingSources(
                    impactTargets,
                    context.Snapshot.ImpactFiles
                        .Select(file => new SourceInput(file.RelativePath, file.Content))
                        .ToList())
                : ImpactAnalyzer.CountReferencingFiles(
                    context.Snapshot.RepositoryPath,
                    impactTargets,
                    ImpactCandidateFiles(context));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            referencedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // Lines in the scan are already lines of the current file, except where a patch came
        // without its line numbers: those findings keep fragment lines, as they always did.
        var contentByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var displayPath = candidate.File.RelativePath.Replace('\\', '/');
            while (displayPath.StartsWith("./", StringComparison.Ordinal))
            {
                displayPath = displayPath[2..];
            }

            contentByFile[displayPath] = candidate.File.Content!;
        }

        return MintLintReportFactory.Build(
            scan,
            skippedCount,
            contentByFile,
            referencedBy,
            functionBaselines: analysis.Baselines);
    }

    /// <summary>
    /// The files the impact scan reads. Both lists come from git, so ignored paths such as
    /// build output or the .claude/worktrees checkouts never count as referencers. A full scan
    /// without an untracked list keeps to the tracked files rather than walking the directory.
    /// </summary>
    internal static IReadOnlyList<string>? ImpactCandidateFiles(GitPreflightStepContext context) =>
        context.Request.FullImpactScan && context.Snapshot is { TrackedFiles: { } tracked, UntrackedFiles: { } untracked }
            ? tracked.Concat(untracked).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : context.Snapshot.TrackedFiles;

    private sealed record MintLintChangeCandidate(
        GitStagedFileSnapshot File,
        string AddedContent);
}
