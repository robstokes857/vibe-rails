using System.Text.Json;
using MintLint;
using VibeRails.DTOs;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// What a Code quality scan found, in the words every surface uses: Git Guard's output, the
/// <c>Code quality minimum</c> rule's failure, a lane check's comment and the review report agents
/// read (VIBE-362, VIBE-274). Each file has a letter grade and its problems worst first: a
/// measurement past its own warning or critical limit, where it was measured and how to fix it.
/// The report lists them itself, one per function or type (<see cref="MintLintFileReportResponse.Findings"/>);
/// <see cref="QualityReportMetrics"/> derives them for a report saved before it did. The results are
/// <see cref="QualityFile"/> and <see cref="QualityProblem"/>; <see cref="CodeQualityFixList"/> puts them into words.
/// </summary>
internal static class CodeQualityFindings
{
    internal const string Critical = MetricFinding.CriticalLevel;
    internal const string Warning = MetricFinding.WarningLevel;

    /// <summary>The labels of findings.js's GUIDE table.</summary>
    internal static IReadOnlyDictionary<string, string> Labels => CodeQualityGuide.Labels;
    /// <summary>The fixes of findings.js's GUIDE table.</summary>
    internal static IReadOnlyDictionary<string, string> Fixes => CodeQualityGuide.Fixes;

    /// <summary>Every analyzed file of a saved check's engine result, files that need work first. Empty
    /// when the result carries no MintLint report (VCA, a failed run, an older record).</summary>
    public static IReadOnlyList<QualityFile> Read(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return [];
        try
        {
            var result = JsonSerializer.Deserialize(resultJson, AppJsonSerializerContext.Default.GitPreflightStepResult);
            var json = result?.Details?.GetValueOrDefault(MintLintReportFactory.DetailsKey);
            if (string.IsNullOrWhiteSpace(json)) return [];
            var report = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.MintLintReportResponse);
            return report is null ? [] : FromReport(report);
        }
        catch (JsonException) { return []; }
    }

    internal static IReadOnlyList<QualityFile> FromReport(MintLintReportResponse report) =>
        (report.Files ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file.File))
            .Select(file => (file, result: ToResult(file)))
            .OrderByDescending(pair => RatingRank(pair.result.Rating))
            .ThenByDescending(pair => pair.file.Priority)
            .ThenByDescending(pair => pair.file.Score)
            .ThenBy(pair => pair.result.Path, StringComparer.Ordinal)
            .Select(pair => pair.result)
            .ToList();

    private static QualityFile ToResult(MintLintFileReportResponse file)
    {
        var concern = Math.Clamp(file.Score, 0, 100);
        var problems = Problems(file);
        return new(file.File, file.Rating ?? "", 100 - concern, problems)
        {
            Grade = file.Grade is { Length: 1 } saved ? saved[0] : ScoreCalculator.GradeFor(concern),
            Lines = QualityReportMetrics.Lines(file),
            Closest = problems.Count == 0 ? QualityReportMetrics.Closest(file) : null
        };
    }

    private static int RatingRank(string rating) => rating switch { "AtRisk" => 3, "NeedsWork" => 2, "Okay" => 1, _ => 0 };

    /// <summary>The measurements of one file that are past a limit, worst first: one per declaration when
    /// the report lists findings, otherwise (a report saved before VIBE-274) the worst of each metric.</summary>
    internal static IReadOnlyList<QualityProblem> Problems(MintLintFileReportResponse file) => file.Findings is { } findings
        ? findings.Where(finding => finding is not null && !string.IsNullOrEmpty(finding.Name) && double.IsFinite(finding.Value))
            .Select(ToProblem).ToList()
        : QualityReportMetrics.ProblemsFromCategories(file);

    private static QualityProblem ToProblem(MintLintFindingResponse finding)
    {
        var critical = finding.Level == Critical;
        var limits = new MetricThreshold(finding.Warn, finding.Critical, finding.HigherIsBetter);
        var madeWorse = finding.Previous is double previous && double.IsFinite(previous) && previous != finding.Value;
        return new(finding.Name, CodeQualityGuide.Label(finding.Name), critical ? Critical : Warning, CodeQualityGuide.Measure(finding.Name, finding.Value))
        {
            Source = CodeQualityGuide.Source(finding.Name, finding.Source, finding.Scope),
            Line = finding.Line is > 0 ? finding.Line : null,
            Limit = CodeQualityGuide.Limit(finding.Name, limits, critical),
            Fix = CodeQualityGuide.For(finding.Name)?.Fix,
            Was = madeWorse ? CodeQualityGuide.Measure(finding.Name, finding.Previous!.Value) : null
        };
    }
}
