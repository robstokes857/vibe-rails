using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Which function of the previous version each function of the current file was, so that a
/// change is compared with that function and not with another of the same name. Methods of
/// different types, overloads and anonymous functions all share names, so a name alone cannot
/// say. The lines a function kept say (<see cref="KeptLines"/>), in this order:
/// <list type="number">
/// <item>A function that certainly kept lines of a function of its own name was that function.</item>
/// <item>A function that certainly kept lines only of a function whose name is gone from the
/// file is that function renamed.</item>
/// <item>A function still unplaced was the function of its name it most likely kept lines of
/// (lines placed by their neighbours), one function each, the surest pair first.</item>
/// <item>A function left over takes the first function of its name nobody has claimed, in file
/// order: first the functions that kept some line, then the ones the change wrote every line of
/// (a rewrite, or a file whose every line changed).</item>
/// </list>
/// A function none of these places is new. "File order" is by line: the engine lists a file's
/// functions most complex first, and pairing in that order would swap the histories of two
/// functions whose complexities cross.
/// </summary>
internal sealed class FunctionHistory
{
    private const int None = LineOwners.None;

    private readonly IReadOnlyList<FunctionMetrics> _now;
    private readonly IReadOnlyList<FunctionMetrics> _before;
    private readonly KeptLines _kept;
    private readonly int[] _nowOrder;
    private readonly int[] _beforeOrder;
    private readonly int[] _beforeRank;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly int[] _was;
    private readonly bool[] _claimed;

    private FunctionHistory(FileMetrics now, FileMetrics before, ChangedSource change)
    {
        _now = now.Functions;
        _before = before.Functions;
        _kept = KeptLines.Count(_now, _before, change);
        _nowOrder = LineOwners.InFileOrder(_now);
        _beforeOrder = LineOwners.InFileOrder(_before);
        _beforeRank = new int[_before.Count];
        for (int rank = 0; rank < _beforeOrder.Length; rank++)
        {
            _beforeRank[_beforeOrder[rank]] = rank;
        }

        foreach (FunctionMetrics function in _now)
        {
            _names.Add(function.Name);
        }

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
        history.TakeKept(sameName: true);
        history.TakeKept(sameName: false);
        history.TakeNeighbours();
        history.TakeFreeNamesakes(keptAny: true);
        history.TakeFreeNamesakes(keptAny: false);

        Dictionary<FunctionMetrics, FunctionMetrics> result = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < history._now.Count; i++)
        {
            if (history._was[i] != None)
            {
                result[history._now[i]] = history._before[history._was[i]];
            }
        }

        return result;
    }

    /// <summary>
    /// Rule 1 (<paramref name="sameName"/>) and rule 2: a function is the function it certainly
    /// kept the most lines of. Under another name that is a rename, and only when the old name is
    /// gone from the file: a helper carved out of a function that is still there is new.
    /// </summary>
    private void TakeKept(bool sameName)
    {
        foreach (int i in _nowOrder)
        {
            int origin = _was[i] == None ? MostKept(i, sameName) : None;
            if (origin != None && (sameName || !_names.Contains(_before[origin].Name)))
            {
                Take(i, origin);
            }
        }
    }

    /// <summary>
    /// Rule 3: namesakes that kept only repeated lines (a declaration, braces and a return that
    /// another function also has) are told apart by where those lines sit.
    /// </summary>
    private void TakeNeighbours()
    {
        // Ordered as a tuple: most lines first, then the functions' places in their files.
        List<(int Fewer, int Place, int OriginPlace, int Function, int Origin)> pairs = [];
        for (int place = 0; place < _nowOrder.Length; place++)
        {
            int i = _nowOrder[place];
            foreach ((int origin, int count) in _kept.Likely[i] ?? [])
            {
                if (_was[i] == None && string.Equals(_now[i].Name, _before[origin].Name, StringComparison.Ordinal))
                {
                    pairs.Add((-count, place, _beforeRank[origin], i, origin));
                }
            }
        }

        pairs.Sort();
        foreach ((int _, int _, int _, int function, int origin) in pairs)
        {
            if (_was[function] == None && !_claimed[origin])
            {
                Take(function, origin);
            }
        }
    }

    /// <summary>Rule 4: a function left over takes the first function of its name nobody has claimed, in file order.</summary>
    private void TakeFreeNamesakes(bool keptAny)
    {
        foreach (int i in _nowOrder)
        {
            if (_was[i] == None && _kept.Any[i] == keptAny && !ChangePlacement.IsTopLevel(_now[i]))
            {
                Take(i, FirstFree(_now[i].Name));
            }
        }
    }

    /// <summary>
    /// The previous function, of this function's name or of another, that it certainly kept the
    /// most lines of; the earlier one in the file on a tie.
    /// </summary>
    private int MostKept(int index, bool sameName)
    {
        Dictionary<int, int>? kept = _kept.Certain[index];
        if (kept is null)
        {
            return None;
        }

        int best = None;
        int most = 0;
        foreach (int origin in _beforeOrder)
        {
            bool named = string.Equals(_before[origin].Name, _now[index].Name, StringComparison.Ordinal);
            if (named == sameName && kept.GetValueOrDefault(origin) > most)
            {
                best = origin;
                most = kept[origin];
            }
        }

        return best;
    }

    private int FirstFree(string name)
    {
        foreach (int i in _beforeOrder)
        {
            if (!_claimed[i] && string.Equals(_before[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return None;
    }

    private void Take(int function, int origin)
    {
        if (origin != None)
        {
            _was[function] = origin;
            _claimed[origin] = true;
        }
    }
}
