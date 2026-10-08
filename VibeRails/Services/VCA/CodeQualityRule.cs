using System.Globalization;
using MintLint;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA;

/// <summary>Grades the combined added code in a VCA rule's scope using the Quality scanner.</summary>
public static class CodeQualityRule
{
    /// <summary>Shared catalog description for all supported grade thresholds.</summary>
    public const string Description =
        "Checks the overall Code quality grade of added code in supported source files under this rule file. "
        + "Uses the combined grade, not individual file grades. Report ignore preferences do not disable this rule.";

    /// <summary>Recognizes a quality rule, including malformed hand-written thresholds.</summary>
    public static bool LooksLike(string text) =>
        text.TrimStart().StartsWith("Code quality", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Evaluates one aggregate score. Empty supported changes do not require a grade.</summary>
    public static RuleValidationResult Evaluate(
        char minimumGrade,
        IEnumerable<GitStagedFileSnapshot> files,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<SourceInput>();
        foreach (var file in files)
        {
            if (!file.ExistsInIndex || file.IsBinary || !MintLintAnalyzer.SupportsFile(file.RelativePath))
                continue;
            if (file.Content is null)
                return new(false, $"UNSUPPORTED: Code quality could not read {file.RelativePath}.");

            // Null is the legacy snapshot fallback; empty means a deletion-only change.
            var added = file.AddedContent ?? file.Content;
            if (!string.IsNullOrWhiteSpace(added))
                sources.Add(new SourceInput(file.RelativePath, added));
        }

        if (sources.Count == 0)
            return new(true, "Code quality skipped: no added code in supported source files in this rule's scope.");

        var scan = MintLintScorer.Score(MintLintAnalyzer.AnalyzeSources(sources));
        cancellationToken.ThrowIfCancellationRequested();
        return EvaluateScore(minimumGrade, scan.Overall.Score);
    }

    internal static RuleValidationResult EvaluateScore(char minimumGrade, double concern)
    {
        var requiredHealth = minimumGrade switch
        {
            'A' => 90,
            'B' => 80,
            'C' => 70,
            _ => throw new ArgumentOutOfRangeException(nameof(minimumGrade))
        };
        if (!double.IsFinite(concern))
            return new(false, "UNSUPPORTED: Code quality did not produce a finite overall score.");

        var health = 100 - Math.Clamp(concern, 0, 100);
        var grade = health >= 90 ? 'A' : health >= 80 ? 'B' : health >= 70 ? 'C' : health >= 55 ? 'D' : 'F';
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
        return snapshot.Files.Where(file => file.RelativePath.StartsWith(prefix, comparison));
    }
}
