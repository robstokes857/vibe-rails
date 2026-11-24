using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Where a raw metric crosses from acceptable into concerning. A value at <see cref="Warn"/>
/// scores 50; at <see cref="Critical"/> it scores 100. When <see cref="HigherIsBetter"/> is set
/// (e.g. maintainability index) the scale is inverted, and <see cref="Warn"/> should be the
/// higher of the two numbers.
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
    /// How many independently concerning categories it takes to reach the top of the scale. One
    /// category at its critical limit scores <see cref="CriticalFloor"/>; each further one adds an
    /// equal step, and <see cref="BreadthRank"/> of them score 100. With the default of 4 the
    /// steps are 60, 73.3, 86.7 and 100. 1 makes the worst category the whole score.
    /// </summary>
    public int BreadthRank { get; init; } = 4;

    /// <summary>
    /// Fraction of the single worst weight-adjusted category kept as a floor under the overall
    /// while that category is within its warning limit (a weighted concern of 50 or less).
    /// Measurements that are inside their limits are dampened: the default 0.3 puts a file
    /// whose worst category sits exactly at its warning limit at 15, grade B.
    /// </summary>
    public double DepthFloor { get; init; } = 0.3;

    /// <summary>
    /// The overall concern of a file whose worst category is at its critical limit with a
    /// weight of 1.0. Past the warning limit the floor climbs in a straight line from
    /// <see cref="DepthFloor"/> × 50 to this value, so one critical measurement fails the file
    /// on its own. The default 60 is grade F, rated Needs work or worse. Lighter categories
    /// reach less: a weight of 0.8 at its critical limit gives 42 (D).
    /// </summary>
    public double CriticalFloor { get; init; } = 60;

    /// <summary>
    /// Metrics that are not scored for test files (<see cref="TestFileConvention"/>). A test class
    /// is a list of independent cases, so it has no cohesion to lose, and tests construct what
    /// they test and touch the file system and the clock on purpose.
    /// </summary>
    public IReadOnlySet<string> NotScoredInTests { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "lack_of_cohesion",
        "hard_coded_dependencies",
        "ambient_dependencies",
    };

    // Each pair is set so that a warning marks roughly the worst tenth and critical the worst few
    // percent of the functions, types or files it is measured on (calibrated 2026-11-24 against
    // 12,700 production functions; the tuning journal has the distributions). The complexity,
    // size and nesting limits are the usual published ones and already sat there.
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
            ["halstead_difficulty"] = new(30, 50),
            // Measured over the whole file, so it falls with size: it passes 10 near 280 lines and
            // bottoms out at 0 near 450. Per-member limits (Visual Studio's 20 and 10) would flag
            // a third of all files here.
            ["maintainability_index"] = new(10, 0, HigherIsBetter: true),
            ["lack_of_cohesion"] = new(8, 16),
            ["fan_out"] = new(15, 30),
            ["duplication"] = new(0.20, 0.40),
            ["hard_coded_dependencies"] = new(8, 20),
            ["ambient_dependencies"] = new(8, 20),
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
