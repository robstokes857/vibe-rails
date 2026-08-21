using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Measurement-to-grading bridge. Extracts each file's raw metric values from
/// <see cref="FileMetrics"/> (worst function/class per metric, declaration attribution, the
/// LCOM stateless-class guard) and hands them to an <see cref="IScoreCalculator"/> to grade.
/// No grading math lives here — the complete algorithm is in <see cref="ScoreCalculator"/>.
/// </summary>
public static class MintLintScorer
{
    /// <summary>Scores a single file's metrics.</summary>
    /// <param name="file">The measured file.</param>
    /// <param name="profile">Scoring knob values; <see cref="ScoringProfile.Default"/> when null.</param>
    /// <param name="calculator">Grading algorithm; <see cref="ScoreCalculator.Instance"/> when null.</param>
    public static FileScore Score(FileMetrics file, ScoringProfile? profile = null, IScoreCalculator? calculator = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return (calculator ?? ScoreCalculator.Instance)
            .ScoreFile(ToMeasuredFile(file), profile ?? ScoringProfile.Default);
    }

    /// <summary>
    /// Scores a set of files and returns them worst-first, with an overall roll-up across them.
    /// </summary>
    /// <param name="files">The measured files.</param>
    /// <param name="profile">Scoring knob values; <see cref="ScoringProfile.Default"/> when null.</param>
    /// <param name="calculator">Grading algorithm; <see cref="ScoreCalculator.Instance"/> when null.</param>
    public static ScanResult Score(IReadOnlyList<FileMetrics> files, ScoringProfile? profile = null, IScoreCalculator? calculator = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        IScoreCalculator effectiveCalculator = calculator ?? ScoreCalculator.Instance;
        ScoringProfile effectiveProfile = profile ?? ScoringProfile.Default;

        List<FileScore> scored = [];
        foreach (FileMetrics file in files)
        {
            scored.Add(effectiveCalculator.ScoreFile(ToMeasuredFile(file), effectiveProfile));
        }

        // Compute the scan overall BEFORE sorting so the floating-point summation order
        // matches the pre-refactor implementation exactly (input order, not sorted order).
        OverallScore overall = effectiveCalculator.ScoreScan(scored);

        scored.Sort(static (left, right) =>
        {
            int comparison = right.Overall.Score.CompareTo(left.Overall.Score);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.Compare(left.File, right.File, StringComparison.OrdinalIgnoreCase);
            return comparison != 0
                ? comparison
                : string.Compare(left.File, right.File, StringComparison.Ordinal);
        });

        return new ScanResult(scored, overall);
    }

    private static MeasuredFile ToMeasuredFile(FileMetrics file)
    {
        return new MeasuredFile(file.File, ExtractMetrics(file), ExtractOrigins(file));
    }

    private static Dictionary<string, double> ExtractMetrics(FileMetrics file)
    {
        int maxCyclomatic = 0;
        int maxCognitive = 0;
        double maxDifficulty = 0;
        foreach (FunctionMetrics function in file.Functions)
        {
            maxCyclomatic = Math.Max(maxCyclomatic, function.Cyclomatic);
            maxCognitive = Math.Max(maxCognitive, function.Cognitive);
            maxDifficulty = Math.Max(maxDifficulty, function.HalsteadDifficulty);
        }

        int maxLackOfCohesion = 0;
        int maxMethodCount = 0;
        int maxFieldCount = 0;
        foreach (ClassMetrics type in file.Classes)
        {
            // LCOM4 only measures cohesion of shared state; a stateless utility/holder class
            // has nothing to be incohesive about, so it should not be flagged.
            if (type.FieldCount > 0)
            {
                maxLackOfCohesion = Math.Max(maxLackOfCohesion, type.Lcom4);
            }

            maxMethodCount = Math.Max(maxMethodCount, type.MethodCount);
            maxFieldCount = Math.Max(maxFieldCount, type.FieldCount);
        }

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["lines_of_code"] = file.Loc,
            ["cyclomatic_complexity"] = maxCyclomatic,
            ["cognitive_complexity"] = maxCognitive,
            ["npath_complexity"] = file.NPathMax,
            ["nesting_depth"] = file.MaxNestingDepth,
            ["parameter_count"] = file.MaxParameterCount,
            ["halstead_difficulty"] = maxDifficulty,
            ["maintainability_index"] = file.MaintainabilityIndex,
            ["lack_of_cohesion"] = maxLackOfCohesion,
            ["fan_out"] = file.FanOut,
            ["duplication"] = file.DuplicationRatio,
            ["hard_coded_dependencies"] = file.HardCodedDependencies,
            ["ambient_dependencies"] = file.AmbientDependencies,
            ["method_count"] = maxMethodCount,
            ["field_count"] = maxFieldCount,
        };
    }

    /// <summary>
    /// Pins each function/class-scoped metric maximum to the declaration it came from, so a
    /// report can say "cyclomatic 27 — Evaluate, line 12" instead of just naming the file.
    /// File-level metrics (LOC, fan-out, duplication, …) have no entry.
    /// </summary>
    private static Dictionary<string, MetricOrigin> ExtractOrigins(FileMetrics file)
    {
        Dictionary<string, MetricOrigin> origins = new(StringComparer.Ordinal);

        FunctionMetrics? WorstFunctionBy(Func<FunctionMetrics, double> selector)
        {
            FunctionMetrics? worst = null;
            foreach (FunctionMetrics function in file.Functions)
            {
                if (worst is null || selector(function) > selector(worst))
                {
                    worst = function;
                }
            }

            return worst;
        }

        void AddFunction(string key, FunctionMetrics? function)
        {
            if (function is not null && function.Name != "global")
            {
                origins[key] = new MetricOrigin(function.Name, function.Line);
            }
        }

        AddFunction("cyclomatic_complexity", WorstFunctionBy(static f => f.Cyclomatic));
        AddFunction("cognitive_complexity", WorstFunctionBy(static f => f.Cognitive));
        AddFunction("npath_complexity", WorstFunctionBy(static f => f.NPath));
        AddFunction("nesting_depth", WorstFunctionBy(static f => f.NestingDepth));
        AddFunction("parameter_count", WorstFunctionBy(static f => f.ParameterCount));
        AddFunction("halstead_difficulty", WorstFunctionBy(static f => f.HalsteadDifficulty));

        ClassMetrics? WorstClassBy(Func<ClassMetrics, double> selector, bool requireFields = false)
        {
            ClassMetrics? worst = null;
            foreach (ClassMetrics type in file.Classes)
            {
                if (requireFields && type.FieldCount == 0)
                {
                    continue;
                }

                if (worst is null || selector(type) > selector(worst))
                {
                    worst = type;
                }
            }

            return worst;
        }

        void AddClass(string key, ClassMetrics? type)
        {
            if (type is not null)
            {
                origins[key] = new MetricOrigin(type.Name, type.Line);
            }
        }

        AddClass("lack_of_cohesion", WorstClassBy(static c => c.Lcom4, requireFields: true));
        AddClass("method_count", WorstClassBy(static c => c.MethodCount));
        AddClass("field_count", WorstClassBy(static c => c.FieldCount));

        return origins;
    }
}
