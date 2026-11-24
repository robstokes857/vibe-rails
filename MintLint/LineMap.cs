using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Which line of the previous version each line of the current file was, as far as the text can
/// say. A change comes with the numbers of the lines it added and nothing about the lines it
/// kept, so they are placed here, in two strengths:
/// <list type="bullet">
/// <item><b>Certain.</b> A kept line whose text occurs once in the previous version is that
/// line: it has nowhere else to have come from.</item>
/// <item><b>Likely.</b> A kept line right after a placed one, with the same text as the
/// previous version's next line, is taken to be that next line. That is always one way the
/// change could have been made, though not the only one when lines repeat, so it is walked
/// both down and up and the two are left for the reader to compare.</item>
/// </list>
/// Arrays are indexed by 1-based current line; index 0 is unused.
/// </summary>
/// <param name="Certain"><see cref="Added"/>, <see cref="Unknown"/>, or the 1-based previous line.</param>
/// <param name="Down">For a kept line, the previous line it is taken to be when walking down the file, or <see cref="Unknown"/>.</param>
/// <param name="Up">The same, walking up the file.</param>
internal sealed record LineMap(int[] Certain, int[] Down, int[] Up)
{
    /// <summary>A line the change added.</summary>
    public const int Added = -1;

    /// <summary>A line the change kept that could not be placed.</summary>
    public const int Unknown = 0;

    public static LineMap Of(string[] lines, string[] previous, IReadOnlyList<int> addedLines)
    {
        int[] certain = MarkAdded(lines.Length, addedLines);
        PlaceCertain(certain, lines, previous);
        int[] down = Walk(certain, lines, previous, 1);
        int[] up = Walk(certain, lines, previous, -1);
        return new LineMap(certain, down, up);
    }

    private static int[] MarkAdded(int lineCount, IReadOnlyList<int> addedLines)
    {
        int[] certain = new int[lineCount + 1];
        foreach (int line in addedLines)
        {
            if (line >= 1 && line <= lineCount)
            {
                certain[line] = Added;
            }
        }

        return certain;
    }

    private static void PlaceCertain(int[] certain, string[] lines, string[] previous)
    {
        Dictionary<string, int> onlyLineOf = LinesThatOccurOnce(previous);
        for (int line = 1; line <= lines.Length; line++)
        {
            if (certain[line] != Added && onlyLineOf.TryGetValue(lines[line - 1], out int was))
            {
                certain[line] = was;
            }
        }
    }

    /// <summary>
    /// Places the kept lines by their neighbour, walking down (<paramref name="step"/> 1) or up
    /// (-1). Added lines are stepped over: an insertion moves nothing in the previous version.
    /// </summary>
    private static int[] Walk(int[] certain, string[] lines, string[] previous, int step)
    {
        int[] placed = new int[certain.Length];
        int last = Unknown;
        foreach (int line in KeptInOrder(certain, step))
        {
            string text = lines[line - 1];
            last = certain[line] == Unknown ? Neighbour(previous, last, step, text) : certain[line];
            placed[line] = last;
        }

        return placed;
    }

    private static IEnumerable<int> KeptInOrder(int[] certain, int step)
    {
        int count = certain.Length - 1;
        for (int i = 1; i <= count; i++)
        {
            int line = step > 0 ? i : count + 1 - i;
            if (certain[line] != Added)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// The previous version's line next to <paramref name="last"/> when it has this text;
    /// otherwise <see cref="Unknown"/>. Nothing is next to a line that was not placed.
    /// </summary>
    private static int Neighbour(string[] previous, int last, int step, string text)
    {
        int next = last + step;
        bool exists = last != Unknown && next >= 1 && next <= previous.Length;
        return exists && previous[next - 1] == text ? next : Unknown;
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
}
