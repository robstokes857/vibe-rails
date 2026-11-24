using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// THE one place MintLint turns raw metric values into grades. Every number the library
/// reports — per-metric concern, category scores, the file overall, the scan overall, the
/// letter grades and the rating labels — is computed inside this class, in pipeline order,
/// top to bottom (after stage 1 measures the raw metrics):
///
/// <code>
///   Stage 2  Normalize   raw value → 0–100 concern via warn/critical thresholds
///                        (50 at the warning limit, 100 at the critical limit)
///   Stage 3  Category    worst metric wins within each category
///   Stage 4  Roll-up     the worst BreadthRank weighted categories, each dampened while
///                        inside its warning limit; past it the worst one climbs to
///                        CriticalFloor at its critical limit, and every further category
///                        at its critical limit adds a step, up to 100
///   Stage 5  Grade       health = 100 − concern: A ≥90 · B ≥80 · C ≥70 · D ≥55 · F;
///                        rating: A/B Clean · C Okay · D NeedsWork · F AtRisk;
///                        scan overall = mean of the file overalls, weighted by lines
/// </code>
///
/// Two consequences worth knowing. A file with nothing past a warning limit scores 15 or
/// less, so it grades A or B; a grade of C or lower always comes with a finding. And one
/// measurement at its critical limit in a full-weight category scores 60, an F.
///
/// Tuning knob VALUES (thresholds, weights, BreadthRank, DepthFloor, CriticalFloor) live in
/// <see cref="ScoringProfile"/>; the FORMULAS live here and nowhere else. Document the rationale
/// for changes to either and re-derive the hand-computed numbers in Tests/MintLintTests.
/// </summary>
public sealed class ScoreCalculator : IScoreCalculator
{
    /// <summary>The calculator used when callers do not supply their own.</summary>
    public static ScoreCalculator Instance { get; } = new();

    /// <summary>A weighted category concern at or below this is inside its warning limit.</summary>
    private const double WarningConcern = 50.0;

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
                    !profile.Thresholds.TryGetValue(key, out MetricThreshold? threshold) ||
                    !IsScored(key, file.IsTest, profile))
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

        double overall = RollUp(categories, profile);

        // Stage 5 — rating label.
        return new FileScore(file.File, file.Metrics, categories, new OverallScore(overall, RatingFor(overall)));
    }

    /// <summary>
    /// Stage 4 — roll-up. Two questions, and the worst answer is the file's concern.
    /// <para>
    /// Severity: how bad is the single worst dimension? One measurement at its critical limit
    /// must fail the file on its own. Before 2026-11-24 the worst category was only a 0.3
    /// floor, so a function with cyclomatic complexity 25 capped the file at 30, a C, and
    /// passed every gate.
    /// </para>
    /// <para>
    /// Breadth: are several dimensions bad at once? Each further category at its critical
    /// limit raises the concern, up to 100 when BreadthRank of them are, so a file that is
    /// wrong four ways outranks one that is wrong one way.
    /// </para>
    /// Every position runs through the same curve (see <see cref="Graded"/>): a category inside
    /// its warning limit is dampened by DepthFloor, because "worst metric wins" makes small
    /// values loud, and past the limit it climbs in a straight line to that position's ceiling
    /// (see <see cref="Ceiling"/>). Weights below 1.0 de-emphasize less critical smells.
    /// </summary>
    private static double RollUp(IReadOnlyList<CategoryScore> categories, ScoringProfile profile)
    {
        List<double> weighted = new(categories.Count);
        foreach (CategoryScore category in categories)
        {
            weighted.Add(category.Score * category.Weight);
        }

        weighted.Sort(static (left, right) => right.CompareTo(left));

        double overall = 0;
        int rank = Math.Clamp(profile.BreadthRank, 1, Math.Max(1, weighted.Count));
        for (int position = 1; position <= rank && position <= weighted.Count; position++)
        {
            overall = Math.Max(overall, Graded(weighted[position - 1], profile.DepthFloor, Ceiling(position, rank, profile.CriticalFloor)));
        }

        return Math.Round(Math.Clamp(overall, 0, 100), 1);
    }

    /// <inheritdoc />
    public OverallScore ScoreScan(IReadOnlyList<FileScore> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Weighted by lines of code, so a large file counts for what it is. A plain mean let
        // a few small clean files average a large failing one back up to a passing grade.
        double sum = 0;
        double lines = 0;
        foreach (FileScore file in files)
        {
            double weight = file.Metrics.TryGetValue("lines_of_code", out double loc) && loc >= 1 ? loc : 1;
            sum += file.Overall.Score * weight;
            lines += weight;
        }

        double mean = lines <= 0 ? 0 : Math.Round(sum / lines, 1);
        return new OverallScore(mean, RatingFor(mean));
    }

    /// <summary>
    /// Stage 5: the letter grade for a concern score, read from health (100 − concern):
    /// A from 90, B from 80, C from 70, D from 55, otherwise F. A score that is not a
    /// number grades F.
    /// </summary>
    public static char GradeFor(double concern)
    {
        double health = 100 - Math.Clamp(concern, 0, 100);
        if (health >= 90)
        {
            return 'A';
        }

        if (health >= 80)
        {
            return 'B';
        }

        if (health >= 70)
        {
            return 'C';
        }

        return health >= 55 ? 'D' : 'F';
    }

    /// <summary>The lowest health (100 − concern) that still earns <paramref name="grade"/>; 0 for F.</summary>
    public static double HealthFor(char grade)
    {
        return grade switch
        {
            'A' => 90,
            'B' => 80,
            'C' => 70,
            'D' => 55,
            _ => 0,
        };
    }

    /// <summary>
    /// Whether a raw value is past a limit, in the direction the metric reads: critical at or
    /// past the critical limit, warning past the warning limit, otherwise null. A value exactly
    /// at its warning limit is still inside it.
    /// </summary>
    public static string? LevelOf(double value, MetricThreshold threshold)
    {
        ArgumentNullException.ThrowIfNull(threshold);
        double sign = threshold.HigherIsBetter ? -1 : 1;
        if (sign * value >= sign * threshold.Critical)
        {
            return MetricFinding.CriticalLevel;
        }

        return sign * value > sign * threshold.Warn ? MetricFinding.WarningLevel : null;
    }

    /// <summary>Whether a metric counts toward this file's grade; test files skip <see cref="ScoringProfile.NotScoredInTests"/>.</summary>
    public static bool IsScored(string metric, bool isTest, ScoringProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return !isTest || !profile.NotScoredInTests.Contains(metric);
    }

    /// <summary>
    /// What the <paramref name="position"/>-th worst category scores at its critical limit: the
    /// worst one alone gives <paramref name="criticalFloor"/>, and each further one adds an equal
    /// step up to 100 at <paramref name="rank"/> (60, 73.3, 86.7, 100 by default). With a rank of
    /// 1 the worst category is the whole score.
    /// </summary>
    private static double Ceiling(int position, int rank, double criticalFloor)
    {
        return rank <= 1 ? 100.0 : criticalFloor + (100.0 - criticalFloor) * (position - 1) / (rank - 1);
    }

    /// <summary>
    /// Stage 4's curve for one weighted category concern. Inside the warning limit (50 or
    /// less) the value is multiplied by <paramref name="depthFloor"/>. Past it, the result
    /// climbs in a straight line from there to <paramref name="ceiling"/> at 100.
    /// </summary>
    private static double Graded(double weighted, double depthFloor, double ceiling)
    {
        if (weighted <= WarningConcern)
        {
            return weighted * depthFloor;
        }

        double atWarning = WarningConcern * depthFloor;
        return atWarning + (weighted - WarningConcern) / (100.0 - WarningConcern) * (ceiling - atWarning);
    }

    /// <summary>
    /// Stage 2: 0→warn maps to concern 0–50, warn→critical maps to 50–100, at or past
    /// critical pegs at 100. Higher-is-better metrics (maintainability index) mirror it:
    /// twice the warning limit or more is 0, falling to the warning limit maps to 0–50,
    /// warn→critical maps to 50–100. The result is always higher-is-worse regardless of
    /// the raw direction.
    /// </summary>
    private static double Normalize(double raw, MetricThreshold threshold)
    {
        double score = threshold.HigherIsBetter
            ? NormalizeHigherIsBetter(raw, threshold.Warn, threshold.Critical)
            : NormalizeLowerIsBetter(raw, threshold.Warn, threshold.Critical);
        return Math.Clamp(score, 0.0, 100.0);
    }

    private static double NormalizeLowerIsBetter(double raw, double warn, double critical)
    {
        if (raw <= 0 || warn <= 0)
        {
            return raw > 0 ? 100.0 : 0.0;
        }

        if (raw <= warn)
        {
            return raw / warn * 50.0;
        }

        return raw < critical && critical > warn ? 50.0 + (raw - warn) / (critical - warn) * 50.0 : 100.0;
    }

    private static double NormalizeHigherIsBetter(double raw, double warn, double critical)
    {
        if (raw <= critical)
        {
            return 100.0;
        }

        if (raw < warn)
        {
            return warn > critical ? 50.0 + (warn - raw) / (warn - critical) * 50.0 : 100.0;
        }

        return warn > 0 && raw < warn * 2 ? (warn * 2 - raw) / warn * 50.0 : 0.0;
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

    /// <summary>Stage 5: the rating label of a grade, so the two never disagree.</summary>
    private static string RatingFor(double score)
    {
        return GradeFor(score) switch
        {
            'A' or 'B' => "Clean",
            'C' => "Okay",
            'D' => "NeedsWork",
            _ => "AtRisk",
        };
    }

    private sealed record CategoryDefinition(string Name, string[] Metrics);
}
