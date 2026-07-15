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

        List<CategoryScore> categories = [];
        foreach (CategoryDefinition definition in Categories)
        {
            categories.Add(ScoreCategory(definition, metrics, profile));
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

    private static CategoryScore ScoreCategory(CategoryDefinition definition, IReadOnlyDictionary<string, double> metrics, ScoringProfile profile)
    {
        // Worst signal wins: a category should light up if any of its metrics is concerning,
        // rather than being averaged back down by the metrics that happen to be fine.
        double score = 0;
        foreach (string key in definition.Metrics)
        {
            if (metrics.TryGetValue(key, out double raw) && profile.Thresholds.TryGetValue(key, out MetricThreshold? threshold))
            {
                score = Math.Max(score, Normalize(raw, threshold));
            }
        }

        return new CategoryScore(definition.Name, Math.Round(score, 1));
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
        // The overall reflects the file's worst (weight-adjusted) category, so a file that is
        // severe on a single dimension still surfaces for review rather than being averaged out.
        // Weights below 1.0 de-emphasize less critical smells.
        double worst = 0;
        foreach (CategoryScore category in categories)
        {
            double weight = profile.CategoryWeights.TryGetValue(category.Name, out double configured) ? configured : 1.0;
            worst = Math.Max(worst, category.Score * weight);
        }

        double score = Math.Round(Math.Clamp(worst, 0, 100), 1);
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
