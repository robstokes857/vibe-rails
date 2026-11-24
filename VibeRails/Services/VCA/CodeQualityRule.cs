using System.Globalization;
using MintLint;
using VibeRails.DB;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA;

/// <summary>Grades the combined added code in a VCA rule's scope using the Quality scanner.</summary>
public static class CodeQualityRule
{
    /// <summary>Shared catalog description for all supported grade thresholds.</summary>
    public const string Description =
        "Checks the overall Code quality grade of added code in supported source files under this rule file. "
        + "Uses the combined grade, weighted by added lines, not individual file grades. A failure lists the files and functions to fix. "
        + "Explicit Code quality exemptions apply; report ignore preferences do not disable this rule.";

    // A failed rule names the worst few; the Quality page and a lane check's report have the rest.
    private const int FixFiles = 5, FixProblems = 3;

    /// <summary>Recognizes a quality rule, including malformed hand-written thresholds.</summary>
    public static bool LooksLike(string text) =>
        text.TrimStart().StartsWith("Code quality minimum", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the supported minimum grades; C is the lowest configurable threshold.</summary>
    public static bool TryParse(string text, out char grade)
    {
        foreach (var candidate in "ABC")
        {
            if (text.Trim().Equals($"Code quality minimum {candidate}", StringComparison.OrdinalIgnoreCase))
            {
                grade = candidate;
                return true;
            }
        }

        grade = default;
        return false;
    }

    /// <summary>
    /// Evaluates one aggregate score. Empty supported changes do not require a grade. Below the minimum,
    /// the message goes on to name the files that hold the grade down, each with its problems, where they
    /// are and how to fix them, so whoever reads it (often an agent) can act on it.
    /// </summary>
    public static RuleValidationResult Evaluate(
        char minimumGrade,
        IEnumerable<GitStagedFileSnapshot> files,
        CancellationToken cancellationToken = default)
    {
        var verdict = Grade(minimumGrade, files, cancellationToken, out var fixes);
        return verdict.IsValid || fixes.Count == 0
            ? verdict
            : verdict with { Message = $"{verdict.Message} Fix these first:\n{string.Join('\n', fixes)}" };
    }

    /// <summary>The verdict in one sentence, with what to fix kept apart for callers that list it themselves.</summary>
    private static RuleValidationResult Grade(
        char minimumGrade,
        IEnumerable<GitStagedFileSnapshot> files,
        CancellationToken cancellationToken,
        out IReadOnlyList<string> fixes)
    {
        fixes = [];
        cancellationToken.ThrowIfCancellationRequested();
        var changes = new List<ChangedSource>();
        foreach (var file in files)
        {
            if (!file.ExistsInIndex || file.IsBinary || !MintLintAnalyzer.SupportsFile(file.RelativePath))
                continue;
            if (file.Content is null)
                return new(false, $"UNSUPPORTED: Code quality could not read {file.RelativePath}.");

            // Null is the legacy snapshot fallback; empty means a deletion-only change.
            if (!string.IsNullOrWhiteSpace(file.AddedContent ?? file.Content))
                changes.Add(ChangedCode.ToChange(file));
        }

        if (changes.Count == 0)
            return new(true, "Code quality skipped: no added code in supported source files in this rule's scope.");

        var analysis = ChangeAnalyzer.Analyze(changes);
        var scan = MintLintScorer.Score(analysis.Files);
        cancellationToken.ThrowIfCancellationRequested();
        var verdict = EvaluateScore(minimumGrade, scan.Overall.Score);
        if (!verdict.IsValid && double.IsFinite(scan.Overall.Score))
            fixes = CodeQualityFixList.FixLines(
                CodeQualityFindings.FromReport(MintLintReportFactory.Build(scan, 0, functionBaselines: analysis.Baselines)),
                minimumGrade, FixFiles, FixProblems);
        return verdict;
    }

    /// <summary>
    /// Grades every captured file except explicit exemptions. A lane check's minimum grade uses this,
    /// so it and Git Guard's rule judge the same added code by the same thresholds (VIBE-278).
    /// Report ignore preferences do not apply here, as they do not apply to the rule. The message is
    /// the verdict alone: the check's report lists what to fix from its own saved findings.
    /// </summary>
    /// <remarks>
    /// Unlike the rule, it never skips changed supported source it could not read. The snapshot marks a
    /// file over the 5 MiB text limit or the capture budget as binary, and the rule passes binary files
    /// as no added code; a lane minimum that graded nothing must not pass. Such files fail as not
    /// checked, with <see cref="RuleValidationResult.Metadata"/> listing them.
    /// </remarks>
    public static RuleValidationResult EvaluateSnapshot(char minimumGrade, GitStagedSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var exemptions = snapshot.QualityExemptions ?? [];
        var files = snapshot.Files.Where(file => !CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, exemptions)).ToList();
        var unreadable = files.Where(file => file.ExistsInIndex && file.ChangeKind != GitStagedChangeKind.Deleted
                && MintLintAnalyzer.SupportsFile(file.RelativePath) && (file.IsBinary || file.Content is null))
            .Select(file => file.RelativePath).ToList();
        if (unreadable.Count == 0) return Grade(minimumGrade, files, cancellationToken, out _);
        var named = string.Join(", ", unreadable.Take(5)) + (unreadable.Count > 5 ? $" and {unreadable.Count - 5} more" : "");
        return new(false, $"Code quality could not read {unreadable.Count} changed source file(s): {named}. They are over the "
            + $"5 MiB text limit or the capture budget, or not text, so the minimum grade {minimumGrade} was not checked. "
            + "Exempt a generated file with exempt_code_quality_file if it should not be graded.", unreadable);
    }

    internal static RuleValidationResult EvaluateScore(char minimumGrade, double concern)
    {
        if (minimumGrade is not ('A' or 'B' or 'C'))
            throw new ArgumentOutOfRangeException(nameof(minimumGrade));
        if (!double.IsFinite(concern))
            return new(false, "UNSUPPORTED: Code quality did not produce a finite overall score.");

        // The grade table is MintLint's (ScoreCalculator), so a file's letter on the Quality page and
        // the letter this rule requires are the same scale: A 90, B 80, C 70, D 55.
        var requiredHealth = ScoreCalculator.HealthFor(minimumGrade);
        var health = 100 - Math.Clamp(concern, 0, 100);
        var grade = ScoreCalculator.GradeFor(concern);
        var passed = health >= requiredHealth;
        return new(passed, string.Create(CultureInfo.InvariantCulture,
            $"Overall Code quality grade {grade} (health {health:0.0}/100) {(passed ? "meets" : "is below")} minimum {minimumGrade} ({requiredHealth}/100)."));
    }

    /// <summary>Selects the current paths governed by a rule file, using Git path boundaries.</summary>
    public static IEnumerable<GitStagedFileSnapshot> InScope(GitStagedSnapshot snapshot, string sourceFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFile))!;
        var relative = Path.GetRelativePath(snapshot.RepositoryPath, directory).Replace('\\', '/');
        var prefix = relative == "." ? "" : relative.TrimEnd('/') + "/";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return snapshot.Files.Where(file => file.RelativePath.StartsWith(prefix, comparison)
            && !CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, snapshot.QualityExemptions ?? []));
    }
}
