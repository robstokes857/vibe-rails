using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Attribution for one extracted metric value: the function or class declaration the worst
/// measurement came from, so a report can say "cyclomatic 27 — Evaluate, line 12" instead of
/// only naming the file.
/// </summary>
/// <param name="Source">Function or class name.</param>
/// <param name="Line">1-based line of the declaration.</param>
public sealed record MetricOrigin(string Source, int Line);

/// <summary>
/// One file's measured raw metric values, ready for grading. Produced by
/// <see cref="MintLintScorer"/> from <see cref="FileMetrics"/> (worst-function/worst-class
/// selection, the LCOM stateless-class guard, attribution) so the calculator itself never
/// touches measurement.
/// </summary>
/// <param name="File">Forward-slash relative path of the file.</param>
/// <param name="Metrics">Raw values keyed by snake_case metric name (e.g. <c>cyclomatic_complexity</c>).</param>
/// <param name="Origins">Declaration attribution for function/class-scoped metrics; file-level metrics have no entry.</param>
public sealed record MeasuredFile(
    string File,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyDictionary<string, MetricOrigin> Origins);

/// <summary>
/// The grading algorithm behind every MintLint score. There is exactly one production
/// implementation — <see cref="ScoreCalculator"/> — holding the complete
/// "raw numbers → rated scores" math in a single class, so the whole algorithm can be read
/// and tuned in one place. Measurement (parsing, metric extraction) happens before this
/// seam; presentation (sorting, report building) after it.
/// </summary>
public interface IScoreCalculator
{
    /// <summary>Grades one measured file: normalize → categories → breadth-gated overall → rating.</summary>
    /// <param name="file">The file's extracted raw metric values.</param>
    /// <param name="profile">Threshold/weight/roll-up knob values to grade against.</param>
    FileScore ScoreFile(MeasuredFile file, ScoringProfile profile);

    /// <summary>Combines already-graded files into the scan-level overall score and rating.</summary>
    /// <param name="files">The graded files; order does not matter.</param>
    OverallScore ScoreScan(IReadOnlyList<FileScore> files);
}
