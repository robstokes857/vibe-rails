using MintLint;
using VibeRails.DTOs;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Reads a file report's category measurements: the worst value of each metric, which is all a
/// report saved before VIBE-274 has. From them come the file's weight in the overall grade, the
/// measurement nearest its limit, and that older report's problems, ranked by the rules of the
/// Quality page's <c>code-report/findings.js</c>.
/// </summary>
internal static class QualityReportMetrics
{
    /// <summary>The file's lines of code as measured: the weight it has in the overall grade.</summary>
    public static double Lines(MintLintFileReportResponse file)
    {
        var lines = Measured(file).Where(entry => entry.Metric.Name == "lines_of_code").Select(entry => entry.Metric.Value).FirstOrDefault();
        return double.IsFinite(lines) && lines >= 1 ? lines : 1;
    }

    /// <summary>The measurement nearest its warning limit, for a file with nothing past one:
    /// "Cyclomatic complexity 9 in Parse, line 3 (warning above 10)". Null when nothing is measured.</summary>
    public static string? Closest(MintLintFileReportResponse file)
    {
        var nearest = Measured(file)
            .Where(entry => CodeQualityGuide.For(entry.Metric.Name)?.Composite != true && double.IsFinite(entry.Metric.Score) && entry.Metric.Score > 0)
            .OrderByDescending(entry => entry.Metric.Score * entry.Weight)
            .ThenBy(entry => entry.Metric.Name, StringComparer.Ordinal)
            .Select(entry => entry.Metric).FirstOrDefault();
        return nearest is null || !double.IsFinite(nearest.Warn) ? null
            : CodeQualityFixList.Describe(ToProblem(nearest, "ok", critical: false), markdown: false);
    }

    /// <summary>The worst value of each metric that is past a limit: critical before warning, overshoot
    /// capped at twice the warning-to-critical span, then category weight; the composite index last.</summary>
    public static IReadOnlyList<QualityProblem> ProblemsFromCategories(MintLintFileReportResponse file) => Measured(file)
        .Select(entry => Judge(entry.Metric, entry.Weight))
        .Where(entry => entry.Level is not null)
        .OrderByDescending(entry => CodeQualityGuide.For(entry.Problem.Metric)?.Composite == true ? 0 : 1)
        .ThenByDescending(entry => entry.Level == CodeQualityFindings.Critical ? 2 : 1)
        .ThenByDescending(entry => Math.Min(2, entry.Overshoot))
        .ThenByDescending(entry => entry.Weight)
        .ThenByDescending(entry => entry.Concern)
        .ThenBy(entry => entry.Problem.Label, StringComparer.Ordinal)
        .Select(entry => entry.Problem)
        .ToList();

    /// <summary>Each measured metric once; one that feeds two categories (hard-coded dependencies) keeps the higher weight.</summary>
    private static IEnumerable<(MintLintMetricResponse Metric, double Weight)> Measured(MintLintFileReportResponse file)
    {
        var measured = new Dictionary<string, (MintLintMetricResponse Metric, double Weight)>(StringComparer.Ordinal);
        foreach (var category in (file.Categories ?? []).Where(category => category is not null))
        {
            var weight = double.IsFinite(category.Weight) ? category.Weight : 1;
            foreach (var metric in (category.Metrics ?? []).Where(IsMeasured))
                measured[metric.Name] = measured.TryGetValue(metric.Name, out var known)
                    ? (known.Metric, Math.Max(known.Weight, weight)) : (metric, weight);
        }
        return measured.Values;
    }

    private static bool IsMeasured(MintLintMetricResponse? metric) =>
        metric is not null && !string.IsNullOrEmpty(metric.Name) && double.IsFinite(metric.Value);

    private sealed record Judged(QualityProblem Problem, string? Level, double Overshoot)
    {
        public double Weight { get; init; }
        public double Concern { get; init; }
    }

    private static Judged Judge(MintLintMetricResponse metric, double weight)
    {
        var limits = Limits(metric);
        var hasLimits = double.IsFinite(metric.Warn) && double.IsFinite(metric.Critical);
        var level = hasLimits ? ScoreCalculator.LevelOf(metric.Value, limits) : null;
        return new(ToProblem(metric, level ?? "ok", level == CodeQualityFindings.Critical), level, hasLimits ? Overshoot(metric.Value, limits) : 0)
        {
            Weight = weight,
            Concern = double.IsFinite(metric.Score) ? metric.Score : 0
        };
    }

    private static MetricThreshold Limits(MintLintMetricResponse metric) =>
        new(metric.Warn, metric.Critical, metric.HigherIsBetter || metric.Direction == "HB");

    /// <summary>0 at the warning limit, 1 at the critical limit, whichever way the metric reads.</summary>
    private static double Overshoot(double value, MetricThreshold limits)
    {
        var sign = limits.HigherIsBetter ? -1 : 1;
        var span = sign * (limits.Critical - limits.Warn);
        return span > 0 ? sign * (value - limits.Warn) / span : 0;
    }

    private static QualityProblem ToProblem(MintLintMetricResponse metric, string level, bool critical) =>
        new(metric.Name, CodeQualityGuide.Label(metric.Name), level, CodeQualityGuide.Measure(metric.Name, metric.Value))
        {
            Source = CodeQualityGuide.Source(metric.Name, metric.Source),
            Line = metric.Line is > 0 ? metric.Line : null,
            Limit = CodeQualityGuide.Limit(metric.Name, Limits(metric), critical),
            Fix = CodeQualityGuide.For(metric.Name)?.Fix
        };
}
