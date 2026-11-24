using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// The lines a change kept, counted by the function each is in now and the function it was in.
/// Only a kept line whose text occurs once in the previous version is counted: that line is
/// certain to be the same line, where a brace or a repeated statement could be any of several.
/// A line belongs to the innermost function around it, and top-level code is no function.
/// </summary>
internal static class KeptLines
{
    private const int None = -1;

    /// <summary>
    /// Per function of <paramref name="now"/>, by its index: how many of its lines were in each
    /// function of <paramref name="before"/> (null when none were), and whether any line of it
    /// was there before at all, counted or not.
    /// </summary>
    public static (Dictionary<int, int>?[] Kept, bool[] KeptAny) Count(
        IReadOnlyList<FunctionMetrics> now,
        IReadOnlyList<FunctionMetrics> before,
        ChangedSource change)
    {
        string[] lines = Lines(change.Content);
        string[] previous = Lines(change.PreviousContent ?? string.Empty);
        Dictionary<string, int> onlyLineOf = LinesThatOccurOnce(previous);
        int[] owner = Owners(now, lines.Length);
        int[] previousOwner = Owners(before, previous.Length);
        HashSet<int> added = [.. change.AddedLineNumbers ?? []];

        Dictionary<int, int>?[] kept = new Dictionary<int, int>?[now.Count];
        bool[] keptAny = new bool[now.Count];
        for (int line = 1; line <= lines.Length; line++)
        {
            int function = owner[line];
            if (function == None || added.Contains(line))
            {
                continue;
            }

            keptAny[function] = true;
            if (onlyLineOf.TryGetValue(lines[line - 1], out int previousLine))
            {
                Add(ref kept[function], previousOwner[previousLine]);
            }
        }

        return (kept, keptAny);
    }

    private static void Add(ref Dictionary<int, int>? counts, int origin)
    {
        if (origin != None)
        {
            counts ??= [];
            counts[origin] = counts.GetValueOrDefault(origin) + 1;
        }
    }

    private static string[] Lines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>The 1-based line of each text that occurs exactly once.</summary>
    private static Dictionary<string, int> LinesThatOccurOnce(string[] lines)
    {
        Dictionary<string, int> lineOf = new(StringComparer.Ordinal);
        HashSet<string> repeated = new(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lineOf.TryAdd(lines[i], i + 1))
            {
                repeated.Add(lines[i]);
            }
        }

        foreach (string text in repeated)
        {
            lineOf.Remove(text);
        }

        return lineOf;
    }

    /// <summary>
    /// The innermost function around each line (index 0 is unused), or none for top-level code.
    /// Outer functions are painted first, so a nested function overwrites its own lines.
    /// </summary>
    private static int[] Owners(IReadOnlyList<FunctionMetrics> functions, int lineCount)
    {
        int[] owner = new int[lineCount + 1];
        Array.Fill(owner, None);
        foreach (int index in OuterFirst(functions))
        {
            FunctionMetrics function = functions[index];
            int first = Math.Max(1, function.Line);
            int last = Math.Min(lineCount, function.Line + Math.Max(1, function.Loc) - 1);
            for (int line = first; line <= last; line++)
            {
                owner[line] = index;
            }
        }

        return owner;
    }

    /// <summary>The indexes of the real functions (not top-level code), a function before the ones nested in it.</summary>
    private static List<int> OuterFirst(IReadOnlyList<FunctionMetrics> functions)
    {
        List<int> order = [];
        for (int i = 0; i < functions.Count; i++)
        {
            if (!ChangePlacement.IsTopLevel(functions[i]))
            {
                order.Add(i);
            }
        }

        order.Sort((left, right) =>
        {
            int byLine = functions[left].Line.CompareTo(functions[right].Line);
            int bySpan = functions[right].Loc.CompareTo(functions[left].Loc);
            return byLine != 0 ? byLine : bySpan != 0 ? bySpan : left.CompareTo(right);
        });
        return order;
    }
}
