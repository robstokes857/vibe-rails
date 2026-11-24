using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Which function measurements a change answers for: whole functions it created, what it made
/// worse in functions that existed, and its top-level code. See <see cref="ChangeAnalyzer"/>.
/// </summary>
internal static class ChangedFunctions
{
    /// <summary>
    /// The functions to grade, most complex first: each touched function that is new, each
    /// existing one reduced to what got worse, and the top-level scope of the added lines that
    /// are outside every function.
    /// </summary>
    public static List<FunctionMetrics> Counted(
        IReadOnlyList<FunctionMetrics> touched,
        Dictionary<string, FunctionMetrics>? before,
        FileMetrics? topLevel)
    {
        List<FunctionMetrics> counted = [];
        foreach (FunctionMetrics function in touched)
        {
            FunctionMetrics? was = before?.GetValueOrDefault(function.Name);
            FunctionMetrics? graded = was is null ? function : OnlyWhatGotWorse(function, was);
            if (graded is not null)
            {
                counted.Add(graded);
            }
        }

        // Only the top-level scope: a stray declaration the parser sees in those loose lines is
        // not a function of the file.
        foreach (FunctionMetrics function in topLevel?.Functions ?? [])
        {
            if (function.Name == ChangePlacement.TopLevelName)
            {
                counted.Add(function);
            }
        }

        counted.Sort(MostComplexFirst);
        return counted;
    }

    /// <summary>
    /// The added lines' file and type measurements with their function measurements replaced by
    /// <paramref name="counted"/>. Types were found in the added lines, so their lines are mapped
    /// back to lines of the file.
    /// </summary>
    public static FileMetrics Replace(
        FileMetrics fragment,
        IReadOnlyList<FunctionMetrics> counted,
        IReadOnlyList<int> addedLines,
        MintLintOptions options)
    {
        int cyclomaticSum = 0;
        int cognitiveSum = 0;
        int npathMax = 1;
        int highestCyclomatic = 0;
        int maxNesting = 0;
        int maxParameters = 0;
        foreach (FunctionMetrics function in counted)
        {
            cyclomaticSum += function.Cyclomatic;
            cognitiveSum += function.Cognitive;
            npathMax = Math.Max(npathMax, function.NPath);
            highestCyclomatic = Math.Max(highestCyclomatic, function.Cyclomatic);
            maxNesting = Math.Max(maxNesting, function.NestingDepth);
            maxParameters = Math.Max(maxParameters, function.ParameterCount);
        }

        List<ClassMetrics> classes = new(fragment.Classes.Count);
        foreach (ClassMetrics type in fragment.Classes)
        {
            int index = type.Line - 1;
            classes.Add(index >= 0 && index < addedLines.Count ? type with { Line = addedLines[index] } : type);
        }

        return fragment with
        {
            Functions = counted,
            Classes = classes,
            CyclomaticSum = cyclomaticSum,
            CognitiveSum = cognitiveSum,
            NPathMax = npathMax,
            ComplexityLevel = MetricEngine.ClassifyComplexity(highestCyclomatic, options.WarningThreshold, options.ErrorThreshold),
            MaxNestingDepth = maxNesting,
            MaxParameterCount = maxParameters,
        };
    }

    /// <summary>
    /// The previous version's functions by name. Overloads and anonymous functions share a
    /// name, so each measurement keeps its highest value: a function only counts as worse
    /// when it is worse than every predecessor it could be. Top-level code has no entry: it
    /// is measured on the added lines alone.
    /// </summary>
    public static Dictionary<string, FunctionMetrics> WorstByName(FileMetrics file)
    {
        Dictionary<string, FunctionMetrics> worst = new(StringComparer.Ordinal);
        foreach (FunctionMetrics function in file.Functions)
        {
            if (function.Name == ChangePlacement.TopLevelName)
            {
                continue;
            }

            worst[function.Name] = worst.TryGetValue(function.Name, out FunctionMetrics? known) ? Worst(known, function) : function;
        }

        return worst;
    }

    private static FunctionMetrics Worst(FunctionMetrics left, FunctionMetrics right)
    {
        return left with
        {
            Cyclomatic = Math.Max(left.Cyclomatic, right.Cyclomatic),
            Cognitive = Math.Max(left.Cognitive, right.Cognitive),
            NPath = Math.Max(left.NPath, right.NPath),
            HalsteadDifficulty = Math.Max(left.HalsteadDifficulty, right.HalsteadDifficulty),
            NestingDepth = Math.Max(left.NestingDepth, right.NestingDepth),
            ParameterCount = Math.Max(left.ParameterCount, right.ParameterCount),
        };
    }

    /// <summary>
    /// A function that existed, reduced to the measurements this change made worse; null when it
    /// made none worse. What the function inherited or improved reads 0, so it is neither graded
    /// nor listed: a change answers for the complexity it adds, not for the function it found.
    /// Halstead difficulty is always left out here: it moves with any edit, so it would blame
    /// every touched function.
    /// </summary>
    private static FunctionMetrics? OnlyWhatGotWorse(FunctionMetrics now, FunctionMetrics was)
    {
        FunctionMetrics worse = now with
        {
            Cyclomatic = IfHigher(now.Cyclomatic, was.Cyclomatic),
            Cognitive = IfHigher(now.Cognitive, was.Cognitive),
            NPath = Math.Max(1, IfHigher(now.NPath, was.NPath)),
            NestingDepth = IfHigher(now.NestingDepth, was.NestingDepth),
            ParameterCount = IfHigher(now.ParameterCount, was.ParameterCount),
            HalsteadVolume = 0,
            HalsteadDifficulty = 0,
        };
        int anything = worse.Cyclomatic + worse.Cognitive + worse.NestingDepth + worse.ParameterCount + (worse.NPath - 1);
        return anything > 0 ? worse : null;
    }

    private static int IfHigher(int now, int was)
    {
        return now > was ? now : 0;
    }

    private static int MostComplexFirst(FunctionMetrics left, FunctionMetrics right)
    {
        int comparison = right.Cyclomatic.CompareTo(left.Cyclomatic);
        if (comparison == 0)
        {
            comparison = (left.Name == ChangePlacement.TopLevelName).CompareTo(right.Name == ChangePlacement.TopLevelName);
        }

        if (comparison == 0)
        {
            comparison = left.Line.CompareTo(right.Line);
        }

        return comparison != 0 ? comparison : string.CompareOrdinal(left.Name, right.Name);
    }
}
