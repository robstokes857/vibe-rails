using System.Collections.Generic;

namespace MintLint;

/// <summary>One changed file: what it is now, what the change added to it, and what it was before.</summary>
/// <param name="Path">Logical path; only the extension and the display form matter.</param>
/// <param name="Content">The complete current text.</param>
/// <param name="AddedContent">
/// The lines the change added, joined in source order. Null means the whole of
/// <paramref name="Content"/> is new (a new file, or a caller without a patch).
/// </param>
/// <param name="AddedLineNumbers">
/// One entry per line of <paramref name="AddedContent"/>: its 1-based line in
/// <paramref name="Content"/>. Without it the added lines cannot be placed, and they are
/// measured on their own.
/// </param>
/// <param name="PreviousContent">The complete text before the change; null for a new file or when it is unavailable.</param>
public sealed record ChangedSource(
    string Path,
    string Content,
    string? AddedContent = null,
    IReadOnlyList<int>? AddedLineNumbers = null,
    string? PreviousContent = null)
{
    /// <summary>The text measured for size, dependencies, duplication and types: the added lines, or the whole file when all of it is new.</summary>
    internal string Fragment => AddedContent ?? Content;

    /// <summary>True when the added lines can be placed in the file, so its functions can be told apart from top-level code.</summary>
    internal bool IsPlaceable => AddedContent is not null && AddedLineNumbers is not null;
}

/// <summary>What <see cref="ChangeAnalyzer.Analyze"/> measured.</summary>
/// <param name="Files">One entry per supported changed file with added code, ordered by path: the measurements to grade.</param>
/// <param name="WholeFiles">The same files measured whole, in the same order, for callers that need every declared name.</param>
/// <param name="Baselines">
/// Per file, then per function the change touched, named by its name and line in the current
/// file: the function it was before the change, as it measured then. Only functions that
/// existed before appear, so a report can say what a measurement was.
/// </param>
public sealed record ChangeAnalysis(
    IReadOnlyList<FileMetrics> Files,
    IReadOnlyList<FileMetrics> WholeFiles,
    IReadOnlyDictionary<string, IReadOnlyDictionary<(string Name, int Line), FunctionMetrics>> Baselines);
