using MintLint;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// What to fix, in words: one problem as a sentence, which files are below a minimum grade and in
/// what order to fix them, and the list an agent reads. Shared by Git Guard's Code quality step, the
/// <c>Code quality minimum</c> rule and lane checks, so all three name the same problems the same way.
/// </summary>
internal static class CodeQualityFixList
{
    /// <summary>"in `Parse`, line 12", "in top-level code, line 3" or "in the whole file", for Markdown
    /// (<paramref name="markdown"/>) or plain text. Names come from analysed code: one line, no backticks.</summary>
    internal static string Where(QualityProblem problem, bool markdown)
    {
        if (problem.Source is null) return "in the whole file";
        var line = problem.Line is int number ? $", line {number}" : "";
        if (problem.Source == CodeQualityGuide.TopLevelCode) return $"in top-level code{line}";
        var name = QualityText.OneLine(problem.Source, 80).Replace('`', '\'');
        return markdown ? $"in `{name}`{line}" : $"in {name}{line}";
    }

    /// <summary>One problem as a sentence fragment: "Cognitive complexity 115 in `Parse`, line 1 (critical at 30)",
    /// with "; was 22 before this change" when the function already existed.</summary>
    internal static string Describe(QualityProblem problem, bool markdown) =>
        $"{problem.Label} {(markdown ? $"**{problem.Value}**" : problem.Value)} {Where(problem, markdown)} "
        + $"({problem.Limit}{(problem.Was is null ? "" : $"; was {problem.Was} before this change")})";

    /// <summary>Whether a file is below <paramref name="minimum"/>; with no minimum, below C (graded D or F).</summary>
    internal static bool Below(QualityFile file, char? minimum) => file.Health < ScoreCalculator.HealthFor(minimum ?? 'C');

    /// <summary>
    /// The files below the minimum grade (below C when there is none), in the order to fix them: the ones
    /// that pull the line-weighted overall grade down most come first.
    /// </summary>
    internal static IReadOnlyList<QualityFile> ToFix(IReadOnlyList<QualityFile> files, char? minimum)
    {
        var floor = ScoreCalculator.HealthFor(minimum ?? 'C');
        return files.Where(file => Below(file, minimum))
            .OrderByDescending(file => (floor - file.Health) * file.Lines)
            .ThenBy(file => file.Health)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What to fix, as plain lines an agent can act on: each file below the minimum with its grade, then its
    /// problems with where they are and how to fix them (each fix is spelled out once).
    /// <code>
    /// F src/Orders.cs (health 40/100)
    ///   - Cyclomatic complexity 25 in ProcessBatch, line 380 (critical at 20). Fix: Split it into smaller functions…
    /// </code>
    /// Empty when no file is below the minimum.
    /// </summary>
    internal static IReadOnlyList<string> FixLines(IReadOnlyList<QualityFile> files, char? minimum, int maxFiles, int maxProblems)
    {
        var lines = new List<string>();
        var explained = new HashSet<string>(StringComparer.Ordinal);
        var toFix = ToFix(files, minimum);
        foreach (var file in toFix.Take(maxFiles))
        {
            lines.Add($"{file.Grade} {QualityText.OneLine(file.Path, 300)} (health {file.HealthText}/100)");
            lines.AddRange(ProblemLines(file, maxProblems, explained));
        }
        var more = toFix.Count - maxFiles;
        if (more > 0) lines.Add($"and {more} more {(more == 1 ? "file" : "files")} below {minimum ?? 'C'}");
        return lines;
    }

    /// <summary>One file's problems as list items; a fix is added the first time its metric appears in the list.</summary>
    private static IEnumerable<string> ProblemLines(QualityFile file, int maxProblems, HashSet<string> explained)
    {
        if (file.Problems.Count == 0)
            yield return file.Closest is null
                ? "  - No measurement is past a limit; several are close to theirs."
                : $"  - No measurement is past a limit. Closest: {file.Closest}.";
        foreach (var problem in file.Problems.Take(maxProblems))
        {
            var fix = problem.Fix is not null && explained.Add(problem.Metric) ? $" Fix: {problem.Fix}" : "";
            yield return $"  - {Describe(problem, markdown: false)}.{fix}";
        }
        if (file.Problems.Count > maxProblems)
            yield return $"  - and {file.Problems.Count - maxProblems} more in this file";
    }
}
