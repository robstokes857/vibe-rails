using System.Globalization;
using MintLint;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public sealed class MintLintPreflightStep : IGitPreflightStep
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

        var candidates = context.Snapshot.Files
            .Where(file => file.ExistsInIndex
                && !file.IsBinary
                && file.Content != null
                && MintLintAnalyzer.SupportsFile(file.RelativePath))
            .Select(file => new SourceInput(file.RelativePath, file.Content!))
            .ToList();
        var skippedCount = context.Snapshot.Files.Count - candidates.Count;

        if (candidates.Count == 0)
        {
            var skipped = $"No supported staged source files to scan ({skippedCount} skipped).";
            await context.WriteOutputAsync(skipped, new Dictionary<string, string>
            {
                ["supportedFileCount"] = "0",
                ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
            return new GitPreflightStepResult(
                Id,
                DisplayName,
                GitPreflightStepStatus.Skipped,
                skipped,
                [skipped],
                DurationMs: 0,
                Blocking: false,
                new Dictionary<string, string>
                {
                    ["supportedFileCount"] = "0",
                    ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture)
                });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var analyzed = MintLintAnalyzer.AnalyzeSources(candidates);
        var scan = MintLintScorer.Score(analyzed);

        // Reach across the repository: bad code that many files reference outranks bad
        // code nothing uses. Tracked files only by default; a full-repo scan also walks
        // untracked source. Failure here must never block the report itself.
        IReadOnlyDictionary<string, int> referencedBy;
        try
        {
            referencedBy = ImpactAnalyzer.CountReferencingFiles(
                context.Snapshot.RepositoryPath,
                analyzed,
                context.Request.FullImpactScan ? null : context.Snapshot.TrackedFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            referencedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // Score the committed version of each modified file so priority can distinguish
        // concern this change introduced from debt the file already carried.
        var baselineInputs = context.Snapshot.Files
            .Where(file => file.PreviousContent != null
                && MintLintAnalyzer.SupportsFile(file.RelativePath))
            .Select(file => new SourceInput(file.RelativePath, file.PreviousContent!))
            .ToList();
        var baselineByFile = new Dictionary<string, double>(StringComparer.Ordinal);
        if (baselineInputs.Count > 0)
        {
            foreach (var baselineFile in MintLintAnalyzer.ScanSources(baselineInputs).Files)
            {
                baselineByFile[baselineFile.File] = baselineFile.Overall.Score;
            }
        }

        var contentByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var displayPath = candidate.Path.Replace('\\', '/');
            while (displayPath.StartsWith("./", StringComparison.Ordinal))
            {
                displayPath = displayPath[2..];
            }

            contentByFile[displayPath] = candidate.Content;
        }

        var output = new List<string>
        {
            $"Scanned {scan.Files.Count} supported staged source file(s); skipped {skippedCount}.",
            $"Overall concern: {scan.Overall.Score:0.0}/100 · {scan.Overall.Rating}"
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
        var report = MintLintReportFactory.Build(scan, skippedCount, contentByFile, referencedBy, baselineByFile);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["supportedFileCount"] = scan.Files.Count.ToString(CultureInfo.InvariantCulture),
            ["skippedFileCount"] = skippedCount.ToString(CultureInfo.InvariantCulture),
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
                : $"MintLint finished with a {scan.Overall.Rating} rating.",
            output,
            DurationMs: 0,
            Blocking: false,
            details);
    }
}
