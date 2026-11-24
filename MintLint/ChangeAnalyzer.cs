using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Measures a change instead of whole files, so a commit is graded on what it did.
/// <list type="bullet">
/// <item>Size, dependencies, duplication, the maintainability index and types are measured on
/// the added lines alone: untouched code is never graded.</item>
/// <item>A function the change created is measured whole, at its real line.</item>
/// <item>A function that existed and that the change touched is measured whole too, but only
/// for what the change made worse: a cyclomatic, cognitive or NPath complexity, nesting depth
/// or parameter count higher than that function had. What it inherited, and what it
/// improved, is left out (it reads 0), and a function the change did not make worse in any
/// of them is not graded at all. Which function it was is decided by the lines it kept, not
/// by its name alone (<see cref="FunctionHistory"/>): methods of different types, overloads
/// and anonymous functions share names.</item>
/// <item>Added lines outside every function (imports, fields, script statements) are measured
/// on their own, as top-level code.</item>
/// </list>
/// Lines added inside an existing function are a fragment of it. Measured alone they have no
/// name, no line and no meaning: several such fragments used to be graded as one lump of
/// top-level code, and a refactor that split one function into several failed for it.
/// </summary>
public static class ChangeAnalyzer
{
    /// <summary>Measures the supported files of one change as a batch (duplication is measured across their added lines).</summary>
    /// <param name="changes">The changed files. Unsupported paths and files with no added code are skipped; a repeated path keeps its first entry.</param>
    /// <param name="options">Analysis options; <see cref="MintLintOptions.Default"/> when null.</param>
    public static ChangeAnalysis Analyze(IReadOnlyList<ChangedSource> changes, MintLintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return new ChangeRun(Distinct(changes), options ?? MintLintOptions.Default).Result();
    }

    /// <summary>The changes with added code, by display path; a repeated path keeps its first entry.</summary>
    private static Dictionary<string, ChangedSource> Distinct(IReadOnlyList<ChangedSource> changes)
    {
        Dictionary<string, ChangedSource> byPath = new(StringComparer.Ordinal);
        foreach (ChangedSource change in changes)
        {
            if (change is null)
            {
                throw new ArgumentException("The change collection cannot contain null entries.", nameof(changes));
            }

            if (!string.IsNullOrWhiteSpace(change.Fragment))
            {
                byPath.TryAdd(DisplayPath(change.Path), change);
            }
        }

        return byPath;
    }

    // The analyzer's own display form: forward slashes, no leading "./".
    internal static string DisplayPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
