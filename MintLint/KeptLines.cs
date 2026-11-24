using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// The lines a change kept, counted by the function each is in now and the function it was in,
/// in the two strengths <see cref="LineMap"/> places them with. A line belongs to the innermost
/// function around it, and top-level code is no function. Functions are named by their index in
/// the engine's lists.
/// </summary>
internal sealed class KeptLines
{
    /// <summary>A kept line that could not be placed in the previous version.</summary>
    private const int Unplaced = -2;

    private KeptLines(int functionCount)
    {
        Certain = new Dictionary<int, int>?[functionCount];
        Likely = new Dictionary<int, int>?[functionCount];
        Any = new bool[functionCount];
    }

    /// <summary>Per current function: how many of its lines were certainly in each previous function; null when none.</summary>
    public Dictionary<int, int>?[] Certain { get; }

    /// <summary>
    /// Per current function: how many of its lines were in each previous function, counting the
    /// certain ones and the ones placed by their neighbours; null when none.
    /// </summary>
    public Dictionary<int, int>?[] Likely { get; }

    /// <summary>Per current function: whether any line of it was there before, placed or not.</summary>
    public bool[] Any { get; }

    public static KeptLines Count(IReadOnlyList<FunctionMetrics> now, IReadOnlyList<FunctionMetrics> before, ChangedSource change)
    {
        string[] lines = Lines(change.Content);
        string[] previous = Lines(change.PreviousContent ?? string.Empty);
        LineMap map = LineMap.Of(lines, previous, change.AddedLineNumbers ?? []);
        int[] owner = LineOwners.Of(now, lines.Length);
        int[] previousOwner = LineOwners.Of(before, previous.Length);

        KeptLines kept = new(now.Count);
        for (int line = 1; line <= lines.Length; line++)
        {
            if (owner[line] != LineOwners.None && map.Certain[line] != LineMap.Added)
            {
                kept.Keep(owner[line], Origins(map, line, previousOwner));
            }
        }

        return kept;
    }

    private void Keep(int function, (int Certain, int Likely) origin)
    {
        Any[function] = true;
        Add(ref Certain[function], origin.Certain);
        Add(ref Likely[function], origin.Likely);
    }

    /// <summary>
    /// The previous function a kept line was in: for certain, and as far as its neighbours say.
    /// Walking down and up may place a repeated line differently; that only matters when the two
    /// places are in different functions, and then the line is left out.
    /// </summary>
    private static (int Certain, int Likely) Origins(LineMap map, int line, int[] previousOwner)
    {
        int certain = Origin(previousOwner, map.Certain[line]);
        if (certain != Unplaced)
        {
            return (certain, certain);
        }

        int down = Origin(previousOwner, map.Down[line]);
        int up = Origin(previousOwner, map.Up[line]);
        if (down == Unplaced)
        {
            return (Unplaced, up);
        }

        return (Unplaced, up == Unplaced || up == down ? down : Unplaced);
    }

    private static int Origin(int[] previousOwner, int previousLine)
    {
        return previousLine > 0 ? previousOwner[previousLine] : Unplaced;
    }

    private static void Add(ref Dictionary<int, int>? counts, int origin)
    {
        if (origin >= 0)
        {
            counts ??= [];
            counts[origin] = counts.GetValueOrDefault(origin) + 1;
        }
    }

    private static string[] Lines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n');
    }
}
