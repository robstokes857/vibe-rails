using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>One analysis: the texts of each change are measured in batches, then put together file by file.</summary>
internal sealed class ChangeRun
{
    private readonly Dictionary<string, ChangedSource> _byPath;
    private readonly MintLintOptions _options;
    private readonly Dictionary<string, ChangePlacement> _placements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, FunctionMetrics>> _baselines = new(StringComparer.Ordinal);
    private Dictionary<string, FileMetrics> _topLevel = [];
    private Dictionary<string, FileMetrics> _previous = [];

    public ChangeRun(Dictionary<string, ChangedSource> byPath, MintLintOptions options)
    {
        _byPath = byPath;
        _options = options;
    }

    public ChangeAnalysis Result()
    {
        IReadOnlyList<FileMetrics> fragments = Measure(static change => change.Fragment);
        Dictionary<string, FileMetrics> wholes = ByFile(Measure(static change => change.AddedContent is null ? null : change.Content));
        Place(fragments, wholes);
        // The added lines that sit inside no function are top-level code, measured on their own.
        _topLevel = ByFile(Measure(change => _placements.GetValueOrDefault(ChangeAnalyzer.DisplayPath(change.Path))?.TopLevelText));
        _previous = ByFile(Measure(static change => change.IsPlaceable ? change.PreviousContent : null));

        List<FileMetrics> files = new(fragments.Count);
        List<FileMetrics> wholeFiles = new(fragments.Count);
        foreach (FileMetrics fragment in fragments)
        {
            files.Add(Graded(fragment));
            // With no whole to compare with, everything in the file is new: the fragment is the file.
            wholeFiles.Add(wholes.GetValueOrDefault(fragment.File, fragment));
        }

        return new ChangeAnalysis(files, wholeFiles, _baselines);
    }

    /// <summary>Which added lines sit inside a function, and which functions they touch, for each file whose lines can be placed.</summary>
    private void Place(IReadOnlyList<FileMetrics> fragments, Dictionary<string, FileMetrics> wholes)
    {
        foreach (FileMetrics fragment in fragments)
        {
            ChangedSource change = _byPath[fragment.File];
            if (change.IsPlaceable && wholes.TryGetValue(fragment.File, out FileMetrics? whole))
            {
                _placements[fragment.File] = ChangePlacement.Of(whole, change.AddedContent!, change.AddedLineNumbers!);
            }
        }
    }

    /// <summary>
    /// What one file is graded on. Without a placement (a new file, or added lines that cannot be
    /// placed) that is the added text as it stands; otherwise the added lines' file and type
    /// measurements with the functions the change answers for.
    /// </summary>
    private FileMetrics Graded(FileMetrics fragment)
    {
        string file = fragment.File;
        if (!_placements.TryGetValue(file, out ChangePlacement? placement))
        {
            return fragment;
        }

        Dictionary<string, FunctionMetrics>? before = _previous.TryGetValue(file, out FileMetrics? old) ? ChangedFunctions.WorstByName(old) : null;
        if (before is not null)
        {
            _baselines[file] = before;
        }

        List<FunctionMetrics> counted = ChangedFunctions.Counted(placement.Touched, before, _topLevel.GetValueOrDefault(file));
        return ChangedFunctions.Replace(fragment, counted, _byPath[file].AddedLineNumbers!, _options);
    }

    /// <summary>Measures one text of each change as a batch; a change whose text is null or blank is left out.</summary>
    private IReadOnlyList<FileMetrics> Measure(Func<ChangedSource, string?> text)
    {
        List<SourceInput> inputs = [];
        foreach (ChangedSource change in _byPath.Values)
        {
            if (text(change) is { } content && !string.IsNullOrWhiteSpace(content))
            {
                inputs.Add(new SourceInput(change.Path, content));
            }
        }

        return MintLintAnalyzer.AnalyzeSources(inputs, _options);
    }

    private static Dictionary<string, FileMetrics> ByFile(IReadOnlyList<FileMetrics> files)
    {
        Dictionary<string, FileMetrics> result = new(StringComparer.Ordinal);
        foreach (FileMetrics file in files)
        {
            result.TryAdd(file.File, file);
        }

        return result;
    }
}
