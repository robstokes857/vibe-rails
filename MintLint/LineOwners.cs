using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Where a file's functions are: which function each line is in, and the functions in file
/// order. The engine lists a file's functions most complex first, so anything that depends on
/// where a function is declared has to ask here, not rely on that list's order.
/// </summary>
internal static class LineOwners
{
    /// <summary>No function: top-level code.</summary>
    public const int None = -1;

    /// <summary>
    /// The innermost function around each 1-based line, by its index in
    /// <paramref name="functions"/> (index 0 of the result is unused), or <see cref="None"/>.
    /// </summary>
    public static int[] Of(IReadOnlyList<FunctionMetrics> functions, int lineCount)
    {
        int[] owner = new int[lineCount + 1];
        Array.Fill(owner, None);
        // An enclosing function is painted before the ones nested in it, which then overwrite their own lines.
        foreach (int index in InFileOrder(functions))
        {
            FunctionMetrics function = functions[index];
            if (ChangePlacement.IsTopLevel(function))
            {
                continue;
            }

            int first = Math.Max(1, function.Line);
            int last = Math.Min(lineCount, function.Line + Math.Max(1, function.Loc) - 1);
            for (int line = first; line <= last; line++)
            {
                owner[line] = index;
            }
        }

        return owner;
    }

    /// <summary>The indexes of <paramref name="functions"/> in file order: by line, an enclosing function before the ones nested in it.</summary>
    public static int[] InFileOrder(IReadOnlyList<FunctionMetrics> functions)
    {
        int[] order = new int[functions.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (left, right) => Compare(functions[left], functions[right], left.CompareTo(right)));
        return order;
    }

    private static int Compare(FunctionMetrics left, FunctionMetrics right, int listOrder)
    {
        int byLine = left.Line.CompareTo(right.Line);
        int bySpan = right.Loc.CompareTo(left.Loc);
        if (byLine != 0)
        {
            return byLine;
        }

        return bySpan != 0 ? bySpan : listOrder;
    }
}
