using System;
using System.Collections.Generic;
using System.Linq;

namespace MintLint;

/// <summary>
/// Builds <see cref="FileScore.Findings"/>: every measurement past its warning limit, one entry
/// per declaration, worst first. Judging a value against its limits is the calculator's
/// (<see cref="ScoreCalculator.LevelOf"/>); this only walks the declarations and orders the result.
/// </summary>
internal static class FindingList
{
    // A composite only improves when the measurements it is built from do, so it lists last.
    private const string CompositeMetric = "maintainability_index";

    /// <summary>
    /// The actionable findings before the composite index, critical before warning, then how far
    /// past the warning limit (capped at twice the warning-to-critical span, so NPath's
    /// exponential range cannot drown out the rest), then category weight, then source order.
    /// </summary>
    public static IReadOnlyList<MetricFinding> Build(
        FileMetrics file,
        bool isTest,
        ScoringProfile profile,
        IReadOnlyList<CategoryScore> categories)
    {
        Collector collector = new(profile, isTest);
        foreach (FunctionMetrics function in file.Functions)
        {
            collector.Function(function);
        }

        foreach (ClassMetrics type in file.Classes)
        {
            collector.Type(type);
        }

        collector.File(file);

        Dictionary<string, double> weights = Weights(categories);
        return collector.Findings
            .OrderBy(static finding => finding.Name == CompositeMetric)
            .ThenByDescending(static finding => finding.Level == MetricFinding.CriticalLevel)
            .ThenByDescending(static finding => Math.Min(2, Overshoot(finding)))
            .ThenByDescending(finding => weights.GetValueOrDefault(finding.Name, 1))
            .ThenBy(static finding => finding.Line ?? int.MaxValue)
            .ThenBy(static finding => finding.Name, StringComparer.Ordinal)
            .ThenBy(static finding => finding.Source, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The heaviest category each metric feeds (hard-coded dependencies feeds two).</summary>
    private static Dictionary<string, double> Weights(IReadOnlyList<CategoryScore> categories)
    {
        Dictionary<string, double> weights = new(StringComparer.Ordinal);
        foreach (CategoryScore category in categories)
        {
            foreach (MetricScore metric in category.Metrics)
            {
                weights[metric.Name] = Math.Max(weights.GetValueOrDefault(metric.Name), category.Weight);
            }
        }

        return weights;
    }

    /// <summary>0 at the warning limit, 1 at the critical limit, whichever way the metric reads.</summary>
    private static double Overshoot(MetricFinding finding)
    {
        double span = finding.HigherIsBetter ? finding.Warn - finding.Critical : finding.Critical - finding.Warn;
        if (span <= 0)
        {
            return 0;
        }

        return (finding.HigherIsBetter ? finding.Warn - finding.Value : finding.Value - finding.Warn) / span;
    }

    /// <summary>Judges each measurement of a declaration and keeps the ones past a limit.</summary>
    private sealed class Collector(ScoringProfile profile, bool isTest)
    {
        public List<MetricFinding> Findings { get; } = [];

        public void Function(FunctionMetrics function)
        {
            // Code outside any function is measured too; it has no name and no line.
            bool topLevel = function.Name == "global";
            int? line = topLevel || function.Line <= 0 ? null : function.Line;
            Declaration where = new("function", topLevel ? null : function.Name, line, line + Math.Max(1, function.Loc) - 1);
            Judge("cyclomatic_complexity", function.Cyclomatic, where);
            Judge("cognitive_complexity", function.Cognitive, where);
            Judge("npath_complexity", function.NPath, where);
            Judge("nesting_depth", function.NestingDepth, where);
            Judge("parameter_count", function.ParameterCount, where);
            Judge("halstead_difficulty", function.HalsteadDifficulty, where);
        }

        public void Type(ClassMetrics type)
        {
            Declaration where = new("class", type.Name, type.Line > 0 ? type.Line : null, null);
            // LCOM4 measures the cohesion of shared state; a type without fields has none to lose.
            if (type.FieldCount > 0)
            {
                Judge("lack_of_cohesion", type.Lcom4, where);
            }

            Judge("method_count", type.MethodCount, where);
            Judge("field_count", type.FieldCount, where);
        }

        public void File(FileMetrics file)
        {
            Declaration where = new("file", null, null, null);
            Judge("lines_of_code", file.Loc, where);
            Judge("fan_out", file.FanOut, where);
            Judge("duplication", file.DuplicationRatio, where);
            Judge("hard_coded_dependencies", file.HardCodedDependencies, where);
            Judge("ambient_dependencies", file.AmbientDependencies, where);
            Judge(CompositeMetric, file.MaintainabilityIndex, where);
        }

        private void Judge(string name, double value, Declaration where)
        {
            if (!profile.Thresholds.TryGetValue(name, out MetricThreshold? threshold) ||
                !ScoreCalculator.IsScored(name, isTest, profile) ||
                ScoreCalculator.LevelOf(value, threshold) is not { } level)
            {
                return;
            }

            Findings.Add(new MetricFinding(
                name, value, threshold.Warn, threshold.Critical, threshold.HigherIsBetter, level,
                where.Scope, where.Source, where.Line, where.EndLine));
        }
    }

    private readonly record struct Declaration(string Scope, string? Source, int? Line, int? EndLine);
}
