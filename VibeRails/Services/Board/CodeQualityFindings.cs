using System.Globalization;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.Board;

/// <summary>
/// What a saved Code quality check found, file by file, for the card's comment and the review report
/// agents read (VIBE-362). Every measurement is judged against its own warning and critical limits and
/// ranked worst first by the rules of the Quality page's <c>code-report/findings.js</c>, so the comment,
/// the report and the file reader name the same problems in the same order. The labels are pinned to
/// that file by <c>CodeQualityFindingsTests</c>.
/// </summary>
internal static class CodeQualityFindings
{
    /// <summary>One measurement past its warning or critical limit.</summary>
    /// <param name="Source">The function or type it was measured in; null for a whole-file measurement.</param>
    internal sealed record Problem(string Metric, string Label, string Level, string Value, string? Source, int? Line, string Limit);

    /// <summary>One analyzed file: its rating, health (100 minus the concern score) and problems, worst first.</summary>
    internal sealed record FileResult(string Path, string Rating, double Health, IReadOnlyList<Problem> Problems)
    {
        /// <summary>The check's own definition of a finding: a file rated Needs work or At risk.</summary>
        public bool NeedsWork => Rating is "NeedsWork" or "AtRisk";
        public string RatingLabel => Rating switch { "NeedsWork" => "Needs work", "AtRisk" => "At risk", "" => "Not rated", _ => Rating };
        public string HealthText => Format(Health, 1);
        public int CriticalCount => Problems.Count(problem => problem.Level == CodeQualityFindings.Critical);
    }

    internal const string Critical = "critical";
    internal const string Warning = "warning";
    private const double NpathCap = 1_000_000_000;

    private sealed record Guide(string Label, string Scope, bool Composite = false, bool Percent = false);

    /// <summary>The labels and scopes of findings.js's GUIDE table.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Labels;
    private static readonly Dictionary<string, Guide> Guides = new(StringComparer.Ordinal)
    {
        ["cyclomatic_complexity"] = new("Cyclomatic complexity", "function"),
        ["cognitive_complexity"] = new("Cognitive complexity", "function"),
        ["npath_complexity"] = new("NPath execution paths", "function"),
        ["nesting_depth"] = new("Nesting depth", "function"),
        ["parameter_count"] = new("Parameters per function", "function"),
        ["halstead_difficulty"] = new("Halstead difficulty", "function"),
        ["maintainability_index"] = new("Maintainability index", "file", Composite: true),
        ["lines_of_code"] = new("Lines of code", "file"),
        ["method_count"] = new("Methods per type", "class"),
        ["field_count"] = new("Fields per type", "class"),
        ["lack_of_cohesion"] = new("Lack of cohesion (LCOM4)", "class"),
        ["fan_out"] = new("Fan-out (modules imported)", "file"),
        ["duplication"] = new("Duplicated lines", "file", Percent: true),
        ["hard_coded_dependencies"] = new("Hard-coded dependencies", "file"),
        ["ambient_dependencies"] = new("Ambient dependencies", "file")
    };

    static CodeQualityFindings() => Labels = Guides.ToDictionary(pair => pair.Key, pair => pair.Value.Label, StringComparer.Ordinal);

    /// <summary>Every analyzed file of a saved check's engine result, files that need work first. Empty
    /// when the result carries no MintLint report (VCA, a failed run, an older record).</summary>
    public static IReadOnlyList<FileResult> Read(string? resultJson)
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

    internal static IReadOnlyList<FileResult> FromReport(MintLintReportResponse report) =>
        (report.Files ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file.File))
            .Select(file => (file, result: new FileResult(file.File, file.Rating ?? "", 100 - Math.Clamp(file.Score, 0, 100), Problems(file))))
            .OrderByDescending(pair => RatingRank(pair.result.Rating))
            .ThenByDescending(pair => pair.file.Priority)
            .ThenByDescending(pair => pair.file.Score)
            .ThenBy(pair => pair.result.Path, StringComparer.Ordinal)
            .Select(pair => pair.result)
            .ToList();

    private static int RatingRank(string rating) => rating switch { "AtRisk" => 3, "NeedsWork" => 2, "Okay" => 1, _ => 0 };

    /// <summary>The measurements of one file that are past a limit, worst first. A metric that feeds two
    /// categories (hard-coded dependencies) is judged once, at the higher category weight.</summary>
    internal static IReadOnlyList<Problem> Problems(MintLintFileReportResponse file)
    {
        var measured = new Dictionary<string, (MintLintMetricResponse Metric, double Weight)>(StringComparer.Ordinal);
        foreach (var category in file.Categories ?? [])
        {
            if (category is null) continue;
            foreach (var metric in category.Metrics ?? [])
            {
                if (metric is null || string.IsNullOrEmpty(metric.Name) || !double.IsFinite(metric.Value)) continue;
                var weight = double.IsFinite(category.Weight) ? category.Weight : 1;
                measured[metric.Name] = measured.TryGetValue(metric.Name, out var known)
                    ? (known.Metric, Math.Max(known.Weight, weight)) : (metric, weight);
            }
        }
        return measured.Values
            .Select(entry => Judge(entry.Metric, entry.Weight))
            .Where(entry => entry.Level is not null)
            .OrderByDescending(entry => entry.Composite ? 0 : 1)
            .ThenByDescending(entry => entry.Level == Critical ? 2 : 1)
            .ThenByDescending(entry => Math.Min(2, entry.Overshoot))
            .ThenByDescending(entry => entry.Weight)
            .ThenByDescending(entry => entry.Concern)
            .ThenBy(entry => entry.Problem.Label, StringComparer.Ordinal)
            .Select(entry => entry.Problem)
            .ToList();
    }

    private sealed record Judged(Problem Problem, string? Level, bool Composite, double Overshoot, double Weight, double Concern);

    private static Judged Judge(MintLintMetricResponse metric, double weight)
    {
        var guide = Guides.GetValueOrDefault(metric.Name);
        var label = guide?.Label ?? FallbackLabel(metric.Name);
        var higherIsBetter = metric.HigherIsBetter || metric.Direction == "HB";
        var hasLimits = double.IsFinite(metric.Warn) && double.IsFinite(metric.Critical);
        var sign = higherIsBetter ? -1 : 1;
        string? level = !hasLimits ? null
            : sign * metric.Value >= sign * metric.Critical ? Critical
            : sign * metric.Value > sign * metric.Warn ? Warning : null;
        // 0 at the warning limit, 1 at the critical limit, whichever way the metric reads.
        var (from, to, at) = higherIsBetter ? (-metric.Warn, -metric.Critical, -metric.Value) : (metric.Warn, metric.Critical, metric.Value);
        var overshoot = hasLimits && to > from ? (at - from) / (to - from) : 0;
        var source = string.IsNullOrWhiteSpace(metric.Source) ? guide?.Scope == "function" ? "top-level code" : null : metric.Source.Trim();
        var limit = level == Critical ? $"critical at {Measure(metric.Critical, guide)}"
            : $"warning {(higherIsBetter ? "below" : "above")} {Measure(metric.Warn, guide)}";
        var line = metric.Line is > 0 ? metric.Line : null;
        return new(new(metric.Name, label, level ?? "ok", Measure(metric.Value, guide, metric.Name), source, line, limit),
            level, guide?.Composite == true, overshoot, weight, double.IsFinite(metric.Score) ? metric.Score : 0);
    }

    private static string FallbackLabel(string name)
    {
        var words = name.Replace('_', ' ');
        return words.Length == 0 ? "Measurement" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Measure(double value, Guide? guide, string? name = null)
    {
        if (guide?.Percent == true) return $"{Format(value * 100, 1)}%";
        if (name == "npath_complexity" && value >= NpathCap) return "1B (cap)";
        return Format(value, value >= 100 ? 0 : 1);
    }

    /// <summary>findings.js's <c>formatNumber</c>: en-US grouping, at most <paramref name="digits"/> decimals.</summary>
    internal static string Format(double value, int digits) =>
        double.IsFinite(value) ? value.ToString(digits == 0 ? "#,0" : "#,0." + new string('#', digits), CultureInfo.InvariantCulture) : "—";

    /// <summary>"in `Parse`, line 12", "in top-level code, line 3" or "in the whole file", for Markdown
    /// (<paramref name="markdown"/>) or plain text. Names come from analysed code: one line, no backticks.</summary>
    internal static string Where(Problem problem, bool markdown)
    {
        if (problem.Source is null) return "in the whole file";
        var line = problem.Line is int number ? $", line {number}" : "";
        if (problem.Source == "top-level code") return $"in top-level code{line}";
        var name = BoardPromptComposer.SanitizeLine(problem.Source, 80).Replace('`', '\'');
        return markdown ? $"in `{name}`{line}" : $"in {name}{line}";
    }

    /// <summary>One problem as a sentence fragment: "Cognitive complexity 115 in `Parse`, line 1 (critical at 30)".</summary>
    internal static string Describe(Problem problem, bool markdown) =>
        $"{problem.Label} {(markdown ? $"**{problem.Value}**" : problem.Value)} {Where(problem, markdown)} ({problem.Limit})";
}
