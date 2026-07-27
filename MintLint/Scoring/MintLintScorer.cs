using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Turns raw <see cref="FileMetrics"/> into interpreted <see cref="FileScore"/>s: a flat map
/// of raw metric values, a concern score and rating per smell category, and an overall
/// roll-up. Each raw value is normalized to a 0–100 concern scale via the
/// <see cref="ScoringProfile"/>, so unrelated metrics become comparable.
/// </summary>
public static class MintLintScorer
{
    // Each category names the raw metrics that feed it.
    private static readonly CategoryDefinition[] Categories =
    [
        new("Complexity", ["cyclomatic_complexity", "cognitive_complexity", "npath_complexity", "nesting_depth"]),
        new("Size", ["lines_of_code", "method_count", "field_count", "parameter_count"]),
        new("Cohesion", ["lack_of_cohesion"]),
        new("Coupling", ["fan_out", "hard_coded_dependencies"]),
        new("Testability", ["hard_coded_dependencies", "ambient_dependencies"]),
        new("Duplication", ["duplication"]),
        new("Maintainability", ["maintainability_index", "halstead_difficulty"]),
    ];

    /// <summary>Scores a single file's metrics.</summary>
    public static FileScore Score(FileMetrics file, ScoringProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        profile ??= ScoringProfile.Default;

        Dictionary<string, double> metrics = ExtractMetrics(file);
        Dictionary<string, MetricOrigin> origins = ExtractOrigins(file);

        List<CategoryScore> categories = [];
        foreach (CategoryDefinition definition in Categories)
        {
            categories.Add(ScoreCategory(definition, metrics, origins, profile));
        }

        OverallScore overall = RollUp(categories, profile);
        return new FileScore(file.File, metrics, categories, overall);
    }

    /// <summary>
    /// Scores a set of files and returns them worst-first, with an overall roll-up across them.
    /// </summary>
    public static ScanResult Score(IReadOnlyList<FileMetrics> files, ScoringProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        profile ??= ScoringProfile.Default;

        List<FileScore> scored = [];
        double sum = 0;
        foreach (FileMetrics file in files)
        {
            FileScore score = Score(file, profile);
            scored.Add(score);
            sum += score.Overall.Score;
        }

        scored.Sort(static (left, right) =>
        {
            int comparison = right.Overall.Score.CompareTo(left.Overall.Score);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.Compare(left.File, right.File, StringComparison.OrdinalIgnoreCase);
            return comparison != 0
                ? comparison
                : string.Compare(left.File, right.File, StringComparison.Ordinal);
        });

        double mean = scored.Count == 0 ? 0 : Math.Round(sum / scored.Count, 1);
        return new ScanResult(scored, new OverallScore(mean, RatingFor(mean)));
    }

    private static Dictionary<string, double> ExtractMetrics(FileMetrics file)
    {
        int maxCyclomatic = 0;
        int maxCognitive = 0;
        double maxDifficulty = 0;
        foreach (FunctionMetrics function in file.Functions)
        {
            maxCyclomatic = Math.Max(maxCyclomatic, function.Cyclomatic);
            maxCognitive = Math.Max(maxCognitive, function.Cognitive);
            maxDifficulty = Math.Max(maxDifficulty, function.HalsteadDifficulty);
        }

        int maxLackOfCohesion = 0;
        int maxMethodCount = 0;
        int maxFieldCount = 0;
        foreach (ClassMetrics type in file.Classes)
        {
            // LCOM4 only measures cohesion of shared state; a stateless utility/holder class
            // has nothing to be incohesive about, so it should not be flagged.
            if (type.FieldCount > 0)
            {
                maxLackOfCohesion = Math.Max(maxLackOfCohesion, type.Lcom4);
            }

            maxMethodCount = Math.Max(maxMethodCount, type.MethodCount);
            maxFieldCount = Math.Max(maxFieldCount, type.FieldCount);
        }

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["lines_of_code"] = file.Loc,
            ["cyclomatic_complexity"] = maxCyclomatic,
            ["cognitive_complexity"] = maxCognitive,
            ["npath_complexity"] = file.NPathMax,
            ["nesting_depth"] = file.MaxNestingDepth,
            ["parameter_count"] = file.MaxParameterCount,
            ["halstead_difficulty"] = maxDifficulty,
            ["maintainability_index"] = file.MaintainabilityIndex,
            ["lack_of_cohesion"] = maxLackOfCohesion,
            ["fan_out"] = file.FanOut,
            ["duplication"] = file.DuplicationRatio,
            ["hard_coded_dependencies"] = file.HardCodedDependencies,
            ["ambient_dependencies"] = file.AmbientDependencies,
            ["method_count"] = maxMethodCount,
            ["field_count"] = maxFieldCount,
        };
    }

    private sealed record MetricOrigin(string Source, int Line);

    /// <summary>
    /// Pins each function/class-scoped metric maximum to the declaration it came from, so a
    /// report can say "cyclomatic 27 — Evaluate, line 12" instead of just naming the file.
    /// File-level metrics (LOC, fan-out, duplication, …) have no entry.
    /// </summary>
    private static Dictionary<string, MetricOrigin> ExtractOrigins(FileMetrics file)
    {
        Dictionary<string, MetricOrigin> origins = new(StringComparer.Ordinal);

        FunctionMetrics? WorstFunctionBy(Func<FunctionMetrics, double> selector)
        {
            FunctionMetrics? worst = null;
            foreach (FunctionMetrics function in file.Functions)
            {
                if (worst is null || selector(function) > selector(worst))
                {
                    worst = function;
                }
            }

            return worst;
        }

        void AddFunction(string key, FunctionMetrics? function)
        {
            if (function is not null && function.Name != "global")
            {
                origins[key] = new MetricOrigin(function.Name, function.Line);
            }
        }

        AddFunction("cyclomatic_complexity", WorstFunctionBy(static f => f.Cyclomatic));
        AddFunction("cognitive_complexity", WorstFunctionBy(static f => f.Cognitive));
        AddFunction("npath_complexity", WorstFunctionBy(static f => f.NPath));
        AddFunction("nesting_depth", WorstFunctionBy(static f => f.NestingDepth));
        AddFunction("parameter_count", WorstFunctionBy(static f => f.ParameterCount));
        AddFunction("halstead_difficulty", WorstFunctionBy(static f => f.HalsteadDifficulty));

        ClassMetrics? WorstClassBy(Func<ClassMetrics, double> selector, bool requireFields = false)
        {
            ClassMetrics? worst = null;
            foreach (ClassMetrics type in file.Classes)
            {
                if (requireFields && type.FieldCount == 0)
                {
                    continue;
                }

                if (worst is null || selector(type) > selector(worst))
                {
                    worst = type;
                }
            }

            return worst;
        }

        void AddClass(string key, ClassMetrics? type)
        {
            if (type is not null)
            {
                origins[key] = new MetricOrigin(type.Name, type.Line);
            }
        }

        AddClass("lack_of_cohesion", WorstClassBy(static c => c.Lcom4, requireFields: true));
        AddClass("method_count", WorstClassBy(static c => c.MethodCount));
        AddClass("field_count", WorstClassBy(static c => c.FieldCount));

        return origins;
    }

    private static CategoryScore ScoreCategory(
        CategoryDefinition definition,
        IReadOnlyDictionary<string, double> metrics,
        IReadOnlyDictionary<string, MetricOrigin> origins,
        ScoringProfile profile)
    {
        // Worst signal wins: a category should light up if any of its metrics is concerning,
        // rather than being averaged back down by the metrics that happen to be fine.
        double score = 0;
        List<MetricScore> scoredMetrics = [];
        foreach (string key in definition.Metrics)
        {
            if (metrics.TryGetValue(key, out double raw) && profile.Thresholds.TryGetValue(key, out MetricThreshold? threshold))
            {
                double normalized = Normalize(raw, threshold);
                MetricOrigin? origin = origins.GetValueOrDefault(key);
                scoredMetrics.Add(new MetricScore(
                    key,
                    raw,
                    Math.Round(normalized, 1),
                    threshold.Warn,
                    threshold.Critical,
                    threshold.HigherIsBetter,
                    origin?.Source,
                    origin?.Line,
                    threshold.HigherIsBetter ? MetricDirection.HB : MetricDirection.LB));
                score = Math.Max(score, normalized);
            }
        }

        double weight = profile.CategoryWeights.TryGetValue(definition.Name, out double configured) ? configured : 1.0;
        return new CategoryScore(
            definition.Name,
            Math.Round(score, 1),
            weight,
            scoredMetrics,
            SharedDirection(scoredMetrics));
    }

    /// <summary>
    /// A category reads one way only when every scored metric in it reads that way;
    /// a mixed or empty category is <see cref="MetricDirection.NA"/>.
    /// </summary>
    private static MetricDirection SharedDirection(IReadOnlyList<MetricScore> scoredMetrics)
    {
        MetricDirection shared = MetricDirection.NA;
        foreach (MetricScore metric in scoredMetrics)
        {
            if (metric.Direction == MetricDirection.NA)
            {
                return MetricDirection.NA;
            }

            if (shared == MetricDirection.NA)
            {
                shared = metric.Direction;
            }
            else if (shared != metric.Direction)
            {
                return MetricDirection.NA;
            }
        }

        return shared;
    }

    private static double Normalize(double raw, MetricThreshold threshold)
    {
        double score;
        if (!threshold.HigherIsBetter)
        {
            if (raw <= 0 || threshold.Warn <= 0)
            {
                return raw > 0 ? 100.0 : 0.0;
            }

            if (raw <= threshold.Warn)
            {
                score = raw / threshold.Warn * 50.0;
            }
            else if (raw < threshold.Critical && threshold.Critical > threshold.Warn)
            {
                score = 50.0 + (raw - threshold.Warn) / (threshold.Critical - threshold.Warn) * 50.0;
            }
            else
            {
                score = 100.0;
            }
        }
        else
        {
            if (raw >= threshold.Warn)
            {
                score = 0.0;
            }
            else if (raw >= threshold.Critical && threshold.Warn > threshold.Critical)
            {
                score = (threshold.Warn - raw) / (threshold.Warn - threshold.Critical) * 50.0;
            }
            else
            {
                score = threshold.Critical > 0 ? 50.0 + (threshold.Critical - raw) / threshold.Critical * 50.0 : 100.0;
            }
        }

        return Math.Clamp(score, 0.0, 100.0);
    }

    private static OverallScore RollUp(IReadOnlyList<CategoryScore> categories, ScoringProfile profile)
    {
        // Breadth-gated roll-up: the overall is the BreadthRank-th worst (weight-adjusted)
        // category, so a file only rates badly when several independent smell dimensions are
        // severe at once. Categories individually saturate easily (worst metric wins inside
        // each one), so "worst category wins" here made nearly every real file AtRisk — and
        // when everything is bad, nothing is. The DepthFloor keeps a single catastrophic
        // category from vanishing entirely: it caps how far breadth gating can discount the
        // worst dimension. Weights below 1.0 de-emphasize less critical smells.
        List<double> weighted = new(categories.Count);
        foreach (CategoryScore category in categories)
        {
            weighted.Add(category.Score * category.Weight);
        }

        weighted.Sort(static (left, right) => right.CompareTo(left));

        double score = 0;
        if (weighted.Count > 0)
        {
            int rank = Math.Clamp(profile.BreadthRank, 1, weighted.Count);
            score = Math.Max(weighted[rank - 1], weighted[0] * profile.DepthFloor);
        }

        score = Math.Round(Math.Clamp(score, 0, 100), 1);
        return new OverallScore(score, RatingFor(score));
    }

    private static string RatingFor(double score)
    {
        if (score < 30)
        {
            return "Clean";
        }

        if (score < 55)
        {
            return "Okay";
        }

        return score < 75 ? "NeedsWork" : "AtRisk";
    }

    private sealed record CategoryDefinition(string Name, string[] Metrics);
}
