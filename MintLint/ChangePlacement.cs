using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Where a change's added lines fall in the current file: the functions they touch, and the
/// text of the lines that fall inside no function (top-level code).
/// </summary>
/// <param name="Touched">The whole-file functions with at least one added line inside them.</param>
/// <param name="TopLevelText">The added lines outside every function, in order; empty when there are none.</param>
internal sealed record ChangePlacement(IReadOnlyList<FunctionMetrics> Touched, string TopLevelText)
{
    internal const string TopLevelName = "global";

    public static ChangePlacement Of(FileMetrics whole, string addedContent, IReadOnlyList<int> addedLines)
    {
        int[] sorted = [.. addedLines];
        Array.Sort(sorted);

        List<FunctionMetrics> touched = [];
        HashSet<int> insideFunctions = [];
        foreach (FunctionMetrics function in whole.Functions)
        {
            if (function.Name != TopLevelName && Claim(function, sorted, insideFunctions))
            {
                touched.Add(function);
            }
        }

        return new ChangePlacement(touched, TopLevel(addedContent, addedLines, insideFunctions));
    }

    /// <summary>Marks the added lines inside the function; true when there is at least one.</summary>
    private static bool Claim(FunctionMetrics function, int[] sortedAddedLines, HashSet<int> insideFunctions)
    {
        int last = function.Line + Math.Max(1, function.Loc) - 1;
        bool any = false;
        for (int i = FirstAtOrAfter(sortedAddedLines, function.Line); i < sortedAddedLines.Length && sortedAddedLines[i] <= last; i++)
        {
            insideFunctions.Add(sortedAddedLines[i]);
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The added lines that are inside no function. The added text has one line per line number;
    /// when it does not (a caller's patch and its numbers disagree) no line can be told apart, and
    /// none is taken as top-level.
    /// </summary>
    private static string TopLevel(string addedContent, IReadOnlyList<int> addedLines, HashSet<int> insideFunctions)
    {
        string[] lines = addedContent.Replace("\r\n", "\n").Split('\n');
        int count = lines.Length > addedLines.Count && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (count != addedLines.Count)
        {
            return string.Empty;
        }

        List<string> topLevel = [];
        for (int i = 0; i < count; i++)
        {
            if (!insideFunctions.Contains(addedLines[i]))
            {
                topLevel.Add(lines[i]);
            }
        }

        return string.Join('\n', topLevel);
    }

    private static int FirstAtOrAfter(int[] sorted, int value)
    {
        int index = Array.BinarySearch(sorted, value);
        if (index < 0)
        {
            return ~index;
        }

        while (index > 0 && sorted[index - 1] == value)
        {
            index--;
        }

        return index;
    }
}
