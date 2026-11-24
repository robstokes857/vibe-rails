using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Which function of the previous version each function of the current file was, so that a
/// change is compared with that function and not with another of the same name. Methods of
/// different types, overloads and anonymous functions all share names, so a name alone cannot
/// say. In order:
/// <list type="number">
/// <item>A function that kept lines of a function of its own name was that function
/// (<see cref="KeptLines"/> says which lines count).</item>
/// <item>A function that kept lines only of a function whose name is gone from the file is
/// that function renamed.</item>
/// <item>A function left over takes the first function of its name that nobody kept lines of,
/// in file order: first the functions that kept some line, then the ones the change wrote
/// every line of (a rewrite, or a file whose every line changed).</item>
/// </list>
/// A function none of these places is new.
/// </summary>
internal sealed class FunctionHistory
{
    private const int None = -1;

    private readonly IReadOnlyList<FunctionMetrics> _now;
    private readonly IReadOnlyList<FunctionMetrics> _before;
    private readonly Dictionary<int, int>?[] _kept;
    private readonly bool[] _keptAny;
    private readonly int[] _was;
    private readonly bool[] _claimed;

    private FunctionHistory(FileMetrics now, FileMetrics before, ChangedSource change)
    {
        _now = now.Functions;
        _before = before.Functions;
        (_kept, _keptAny) = KeptLines.Count(_now, _before, change);
        _was = new int[_now.Count];
        Array.Fill(_was, None);
        _claimed = new bool[_before.Count];
    }

    /// <summary>
    /// Each function of <paramref name="now"/> that existed, with the function it was. A function
    /// that is not in the result is new. Top-level code is never in it.
    /// </summary>
    /// <param name="now">The current file, measured whole.</param>
    /// <param name="before">The previous version, measured whole.</param>
    /// <param name="change">The change, with its previous text and the numbers of its added lines.</param>
    public static Dictionary<FunctionMetrics, FunctionMetrics> Of(FileMetrics now, FileMetrics before, ChangedSource change)
    {
        FunctionHistory history = new(now, before, change);
        history.TakeKeptNamesakes();
        history.TakeRenamed();
        history.TakeFreeNamesakes(keptAny: true);
        history.TakeFreeNamesakes(keptAny: false);
        return history.Result();
    }

    private Dictionary<FunctionMetrics, FunctionMetrics> Result()
    {
        Dictionary<FunctionMetrics, FunctionMetrics> result = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _now.Count; i++)
        {
            if (_was[i] != None)
            {
                result[_now[i]] = _before[_was[i]];
            }
        }

        return result;
    }

    /// <summary>Rule 1: a function that kept lines of a function of its own name was that function.</summary>
    private void TakeKeptNamesakes()
    {
        for (int i = 0; i < _now.Count; i++)
        {
            int origin = MostKept(i, sameName: true);
            if (origin != None)
            {
                _was[i] = origin;
                _claimed[origin] = true;
            }
        }
    }

    /// <summary>Rule 2: another name on the lines of a function whose name is gone is a rename.</summary>
    private void TakeRenamed()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (FunctionMetrics function in _now)
        {
            names.Add(function.Name);
        }

        for (int i = 0; i < _now.Count; i++)
        {
            int origin = _was[i] == None ? MostKept(i, sameName: false) : None;
            if (origin != None && !names.Contains(_before[origin].Name))
            {
                _was[i] = origin;
                _claimed[origin] = true;
            }
        }
    }

    /// <summary>Rule 3: a function left over takes the first function of its name that nobody kept lines of.</summary>
    private void TakeFreeNamesakes(bool keptAny)
    {
        for (int i = 0; i < _now.Count; i++)
        {
            if (_was[i] != None || _keptAny[i] != keptAny || ChangePlacement.IsTopLevel(_now[i]))
            {
                continue;
            }

            int origin = FirstFree(_now[i].Name);
            if (origin != None)
            {
                _was[i] = origin;
                _claimed[origin] = true;
            }
        }
    }

    /// <summary>The previous function the most lines of function <paramref name="index"/> were in, of its own name or of another; the earlier one on a tie.</summary>
    private int MostKept(int index, bool sameName)
    {
        int best = None;
        int most = 0;
        foreach ((int origin, int count) in _kept[index] ?? [])
        {
            bool named = string.Equals(_before[origin].Name, _now[index].Name, StringComparison.Ordinal);
            bool better = count > most || (count == most && origin < best);
            if (named == sameName && better)
            {
                best = origin;
                most = count;
            }
        }

        return best;
    }

    private int FirstFree(string name)
    {
        for (int i = 0; i < _before.Count; i++)
        {
            if (!_claimed[i] && string.Equals(_before[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return None;
    }
}
