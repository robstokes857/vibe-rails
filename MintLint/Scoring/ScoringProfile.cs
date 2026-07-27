using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Where a raw metric crosses from acceptable into concerning. A value at or below
/// <see cref="Warn"/> scores up to 50; at <see cref="Critical"/> it scores 100. When
/// <see cref="HigherIsBetter"/> is set (e.g. maintainability index) the scale is inverted,
/// and <see cref="Warn"/> should be the higher of the two numbers.
/// </summary>
public sealed record MetricThreshold(double Warn, double Critical, bool HigherIsBetter = false);

/// <summary>
/// The tunable knobs for scoring: per-metric thresholds and per-category weights. Construct
/// a copy with <c>with { … }</c> to adjust scoring without touching the analyzer. This is the
/// single place the scoring system is meant to be tweaked.
/// </summary>
public sealed record ScoringProfile
{
    /// <summary>The default profile with reasonable thresholds for general-purpose code.</summary>
    public static ScoringProfile Default { get; } = new();

    /// <summary>Per-metric thresholds, keyed by the snake_case metric name.</summary>
    public IReadOnlyDictionary<string, MetricThreshold> Thresholds { get; init; } = DefaultThresholds();

    /// <summary>Relative weight of each category when computing the overall score.</summary>
    public IReadOnlyDictionary<string, double> CategoryWeights { get; init; } = DefaultCategoryWeights();

    /// <summary>
    /// How many independently concerning categories it takes before a file's overall score
    /// reflects them: the overall is the <see cref="BreadthRank"/>-th worst weight-adjusted
    /// category. With the default of 4, a file only rates AtRisk when at least four separate
    /// smell dimensions are severe at the same time; one or two hot categories no longer
    /// condemn the whole file. 1 restores the old "worst category wins" behavior.
    /// </summary>
    public int BreadthRank { get; init; } = 4;

    /// <summary>
    /// Fraction of the single worst weight-adjusted category kept as a floor under the
    /// breadth-gated overall, so one catastrophic dimension is dampened rather than erased.
    /// The default 0.3 pins a fully saturated lone category at 30 — the bottom of the Okay
    /// band — instead of letting it read as Clean.
    /// </summary>
    public double DepthFloor { get; init; } = 0.3;

    private static Dictionary<string, MetricThreshold> DefaultThresholds()
    {
        return new Dictionary<string, MetricThreshold>(StringComparer.Ordinal)
        {
            ["lines_of_code"] = new(300, 600),
            ["cyclomatic_complexity"] = new(10, 20),
            ["cognitive_complexity"] = new(15, 30),
            ["npath_complexity"] = new(1000, 100000),
            ["nesting_depth"] = new(4, 6),
            ["parameter_count"] = new(4, 7),
            ["halstead_difficulty"] = new(20, 40),
            ["maintainability_index"] = new(50, 25, HigherIsBetter: true),
            ["lack_of_cohesion"] = new(2, 4),
            ["fan_out"] = new(15, 30),
            ["duplication"] = new(0.10, 0.30),
            ["hard_coded_dependencies"] = new(3, 8),
            ["ambient_dependencies"] = new(3, 10),
            ["method_count"] = new(15, 30),
            ["field_count"] = new(10, 20),
        };
    }

    private static Dictionary<string, double> DefaultCategoryWeights()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Complexity"] = 1.0,
            ["Size"] = 0.7,
            ["Cohesion"] = 1.0,
            ["Coupling"] = 0.8,
            ["Testability"] = 1.0,
            ["Duplication"] = 0.6,
            ["Maintainability"] = 0.8,
        };
    }
}
