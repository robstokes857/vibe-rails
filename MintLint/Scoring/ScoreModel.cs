using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>A headline score (0 = clean, 100 = alarming) with a human-readable rating.</summary>
/// <param name="Score">Concern score from 0 (no concern) to 100 (severe).</param>
/// <param name="Rating">Qualitative label for the grade: Clean (A or B), Okay (C), NeedsWork (D) or AtRisk (F).</param>
public sealed record OverallScore(double Score, string Rating)
{
    /// <summary>Health from 0 to 100: 100 minus the concern score. This is the number a grade is read from.</summary>
    public double Health => 100 - Math.Clamp(Score, 0, 100);

    /// <summary>The letter grade for <see cref="Health"/>: A from 90, B from 80, C from 70, D from 55, otherwise F.</summary>
    public char Grade => ScoreCalculator.GradeFor(Score);
}

/// <summary>
/// Which way a RAW measured value reads, so a UI can say "higher is better" next to
/// quality-named metrics (a maintainability index of 0 is bad; a complexity of 0 is good).
/// The normalized concern score is always higher-is-worse regardless of this.
/// </summary>
public enum MetricDirection
{
    /// <summary>Direction unknown or not applicable.</summary>
    NA,

    /// <summary>Lower raw values are healthier (e.g. cyclomatic complexity).</summary>
    LB,

    /// <summary>Higher raw values are healthier (e.g. maintainability index).</summary>
    HB
}

/// <summary>
/// How one raw metric was interpreted: the measured value, the thresholds it was judged
/// against, and the normalized 0–100 concern score that resulted. This is the finest
/// grain of "how the score was made" and is what UIs should show per metric.
/// </summary>
/// <param name="Name">snake_case metric name (e.g. <c>cyclomatic_complexity</c>).</param>
/// <param name="Value">The raw measured value.</param>
/// <param name="Score">Normalized concern score from 0 (clean) to 100 (severe).</param>
/// <param name="Warn">Threshold where concern reaches 50.</param>
/// <param name="Critical">Threshold where concern reaches 100.</param>
/// <param name="HigherIsBetter">True when larger raw values are healthier (e.g. maintainability index).</param>
/// <param name="Source">Name of the function or class the worst value came from; null for file-level metrics.</param>
/// <param name="Line">1-based line of <paramref name="Source"/>; null when unattributed.</param>
/// <param name="Direction">Which way the raw value reads; see <see cref="MetricDirection"/>.</param>
public sealed record MetricScore(
    string Name,
    double Value,
    double Score,
    double Warn,
    double Critical,
    bool HigherIsBetter,
    string? Source = null,
    int? Line = null,
    MetricDirection Direction = MetricDirection.NA);

/// <summary>
/// The score for one smell category (Complexity, Testability, Cohesion, …), including the
/// per-metric breakdown it was combined from. The category score is its worst metric score;
/// <see cref="Weight"/> is applied when categories roll up into the file's overall score.
/// </summary>
/// <param name="Name">Category name.</param>
/// <param name="Score">Concern score from 0 (clean) to 100 (severe), combined from the category's metrics.</param>
/// <param name="Weight">Relative weight of this category in the overall roll-up.</param>
/// <param name="Metrics">The scored metrics that fed this category.</param>
/// <param name="Direction">
/// The shared direction of this category's raw metrics — <see cref="MetricDirection.NA"/>
/// when the category mixes directions or scored nothing.
/// </param>
public sealed record CategoryScore(
    string Name,
    double Score,
    double Weight,
    IReadOnlyList<MetricScore> Metrics,
    MetricDirection Direction = MetricDirection.NA)
{
    /// <summary>The category's contribution to the overall roll-up (score × weight).</summary>
    public double WeightedScore => Math.Round(Score * Weight, 1);
}

/// <summary>
/// The score for a single file: the raw metric values, the interpreted category scores,
/// and an overall roll-up.
/// </summary>
/// <param name="File">Forward-slash relative path of the file.</param>
/// <param name="Metrics">Raw measured values keyed by snake_case name (e.g. <c>cyclomatic_complexity</c>).</param>
/// <param name="Categories">Interpreted per-category scores.</param>
/// <param name="Overall">Roll-up across categories: the worst category's severity floor, or the breadth term when more categories are hot.</param>
public sealed record FileScore(
    string File,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<CategoryScore> Categories,
    OverallScore Overall)
{
    /// <summary>
    /// Every function, type and file-level measurement past its warning limit, worst first. The
    /// categories carry only the worst value of each metric; this list names each declaration
    /// that needs work, so a fix can be made in one pass.
    /// </summary>
    public IReadOnlyList<MetricFinding> Findings { get; init; } = [];
}

/// <summary>
/// One measurement past its warning or critical limit, pinned to the declaration it was measured in.
/// </summary>
/// <param name="Name">snake_case metric name (e.g. <c>cyclomatic_complexity</c>).</param>
/// <param name="Value">The raw measured value.</param>
/// <param name="Warn">The metric's warning limit.</param>
/// <param name="Critical">The metric's critical limit.</param>
/// <param name="HigherIsBetter">True when larger raw values are healthier.</param>
/// <param name="Level"><see cref="CriticalLevel"/> at or past the critical limit, otherwise <see cref="WarningLevel"/>.</param>
/// <param name="Scope">What was measured: <c>function</c>, <c>class</c> or <c>file</c>.</param>
/// <param name="Source">The function or type name; null for a file-level measurement and for code outside any function.</param>
/// <param name="Line">1-based line where <paramref name="Source"/> is declared; null when unattributed.</param>
/// <param name="EndLine">1-based last line of a function; null otherwise.</param>
public sealed record MetricFinding(
    string Name,
    double Value,
    double Warn,
    double Critical,
    bool HigherIsBetter,
    string Level,
    string Scope,
    string? Source = null,
    int? Line = null,
    int? EndLine = null)
{
    /// <summary>The <see cref="Level"/> of a value at or past its critical limit.</summary>
    public const string CriticalLevel = "critical";

    /// <summary>The <see cref="Level"/> of a value past its warning limit and short of critical.</summary>
    public const string WarningLevel = "warning";
}

/// <summary>The result of scoring a set of files, plus an overall roll-up across them.</summary>
/// <param name="Files">Per-file scores, ordered by descending overall concern (worst first).</param>
/// <param name="Overall">Overall concern across all files: the mean of the file scores, weighted by each file's lines of code.</param>
public sealed record ScanResult(IReadOnlyList<FileScore> Files, OverallScore Overall);
