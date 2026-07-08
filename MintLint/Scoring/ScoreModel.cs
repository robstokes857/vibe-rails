using System.Collections.Generic;

namespace MintLint;

/// <summary>A headline score (0 = clean, 100 = alarming) with a human-readable rating.</summary>
/// <param name="Score">Concern score from 0 (no concern) to 100 (severe).</param>
/// <param name="Rating">Qualitative label for the score band.</param>
public sealed record OverallScore(double Score, string Rating);

/// <summary>
/// The score for one smell category (Complexity, Testability, Cohesion, …).
/// </summary>
/// <param name="Name">Category name.</param>
/// <param name="Score">Concern score from 0 (clean) to 100 (severe), combined from the category's metrics.</param>
public sealed record CategoryScore(string Name, double Score);

/// <summary>
/// The score for a single file: the raw metric values, the interpreted category scores,
/// and an overall roll-up.
/// </summary>
/// <param name="File">Forward-slash relative path of the file.</param>
/// <param name="Metrics">Raw measured values keyed by snake_case name (e.g. <c>cyclomatic_complexity</c>).</param>
/// <param name="Categories">Interpreted per-category scores.</param>
/// <param name="Overall">Weighted roll-up across categories.</param>
public sealed record FileScore(
    string File,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<CategoryScore> Categories,
    OverallScore Overall);

/// <summary>The result of scoring a set of files, plus an overall roll-up across them.</summary>
/// <param name="Files">Per-file scores, ordered by descending overall concern (worst first).</param>
/// <param name="Overall">Mean overall concern across all files.</param>
public sealed record ScanResult(IReadOnlyList<FileScore> Files, OverallScore Overall);
