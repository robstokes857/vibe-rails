using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// THE one place MintLint turns raw metric values into grades. Every number the library
/// reports — per-metric concern, category scores, the file overall, the scan overall, and
/// the rating labels — is computed inside this class, in pipeline order, top to bottom
/// (stage numbers follow MintLint/MintLintAlgo.md):
///
/// <code>
///   Stage 2  Normalize   raw value → 0–100 concern via warn/critical thresholds
///   Stage 3  Category    worst metric wins within each category
///   Stage 4  Roll-up     breadth-gated: the BreadthRank-th worst weighted category,
///                        floored at DepthFloor × the single worst
///   Stage 5  Rating      &lt;30 Clean · &lt;55 Okay · &lt;75 NeedsWork · ≥75 AtRisk;
///                        scan overall = mean of the file overalls
/// </code>
///
/// Tuning knob VALUES (thresholds, weights, BreadthRank, DepthFloor) live in
/// <see cref="ScoringProfile"/>; the FORMULAS live here and nowhere else. Log any change to
/// either in MintLintAlgo.md (the tuning journal) and re-derive the hand-computed numbers
/// in Tests/MintLintTests.
/// </summary>
public sealed class ScoreCalculator : IScoreCalculator
{
    /// <summary>The calculator used when callers do not supply their own.</summary>
    public static ScoreCalculator Instance { get; } = new();

    // Which raw metrics feed which category. This wiring is part of the algorithm (moving a
    // metric between categories changes grades), so it lives here with the math rather than
    // in the profile.
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

    /// <inheritdoc />
    public FileScore ScoreFile(MeasuredFile file, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(profile);

        // Stages 2 + 3 — normalize every raw value, then let the worst signal win inside
        // each category: a category should light up if ANY of its metrics is concerning,
        // rather than being averaged back down by the metrics that happen to be fine.
        List<CategoryScore> categories = new(Categories.Length);
        foreach (CategoryDefinition definition in Categories)
        {
            double worst = 0;
            List<MetricScore> scoredMetrics = [];
            foreach (string key in definition.Metrics)
            {
                if (!file.Metrics.TryGetValue(key, out double raw) ||
                    !profile.Thresholds.TryGetValue(key, out MetricThreshold? threshold))
                {
                    continue;
                }

                double normalized = Normalize(raw, threshold);
                MetricOrigin? origin = file.Origins.GetValueOrDefault(key);
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

                // Track the worst UNROUNDED score and round once at the end; taking the max
                // of the rounded per-metric scores instead would drift the pinned totals.
                worst = Math.Max(worst, normalized);
            }

            double weight = profile.CategoryWeights.TryGetValue(definition.Name, out double configured) ? configured : 1.0;
            categories.Add(new CategoryScore(
                definition.Name,
                Math.Round(worst, 1),
                weight,
                scoredMetrics,
                SharedDirection(scoredMetrics)));
        }

        // Stage 4 — breadth-gated roll-up: the overall is the BreadthRank-th worst
        // (weight-adjusted) category, so a file only rates badly when several independent
        // smell dimensions are severe at once. Categories individually saturate easily
        // (worst metric wins inside each one), so "worst category wins" here made nearly
        // every real file AtRisk — and when everything is bad, nothing is. The DepthFloor
        // keeps a single catastrophic category from vanishing entirely: it caps how far
        // breadth gating can discount the worst dimension. Weights below 1.0 de-emphasize
        // less critical smells.
        List<double> weighted = new(categories.Count);
        foreach (CategoryScore category in categories)
        {
            weighted.Add(category.Score * category.Weight);
        }

        weighted.Sort(static (left, right) => right.CompareTo(left));

        double overall = 0;
        if (weighted.Count > 0)
        {
            int rank = Math.Clamp(profile.BreadthRank, 1, weighted.Count);
            overall = Math.Max(weighted[rank - 1], weighted[0] * profile.DepthFloor);
        }

        overall = Math.Round(Math.Clamp(overall, 0, 100), 1);

        // Stage 5 — rating label.
        return new FileScore(file.File, file.Metrics, categories, new OverallScore(overall, RatingFor(overall)));
    }

    /// <inheritdoc />
    public OverallScore ScoreScan(IReadOnlyList<FileScore> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        double sum = 0;
        foreach (FileScore file in files)
        {
            sum += file.Overall.Score;
        }

        double mean = files.Count == 0 ? 0 : Math.Round(sum / files.Count, 1);
        return new OverallScore(mean, RatingFor(mean));
    }

    /// <summary>
    /// Stage 2: 0→warn maps to concern 0–50, warn→critical maps to 50–100, at or past
    /// critical pegs at 100. Higher-is-better metrics (maintainability index) invert the
    /// scale. The result is always higher-is-worse regardless of the raw direction.
    /// </summary>
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

    /// <summary>Stage 5: the score bands behind the rating labels.</summary>
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
