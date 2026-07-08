using System.Collections.Generic;

namespace MintLint;

/// <summary>Metrics for a single function or method.</summary>
/// <param name="Name">Function name; <c>"global"</c> for top-level code outside any function, or <c>"anonymous"</c> for unnamed functions.</param>
/// <param name="Line">1-based start line (0 for the synthetic global scope).</param>
/// <param name="Loc">Lines spanned by the function.</param>
/// <param name="Cyclomatic">Cyclomatic complexity (independent decision points plus one).</param>
/// <param name="Cognitive">Cognitive complexity (decision points weighted by nesting depth).</param>
/// <param name="NPath">NPath complexity (count of acyclic execution paths, capped to avoid overflow).</param>
/// <param name="HalsteadVolume">Halstead volume of the function.</param>
/// <param name="HalsteadDifficulty">Halstead difficulty of the function.</param>
public sealed record FunctionMetrics(
    string Name,
    int Line,
    int Loc,
    int Cyclomatic,
    int Cognitive,
    int NPath,
    double HalsteadVolume,
    double HalsteadDifficulty);

/// <summary>Metrics for a single class-like type (class, struct, interface, or record).</summary>
/// <param name="Name">Type name, or <c>"anonymous"</c> if it could not be resolved.</param>
/// <param name="Line">1-based line of the declaration.</param>
/// <param name="MethodCount">Number of methods declared on the type.</param>
/// <param name="FieldCount">Number of distinct fields/properties declared on the type.</param>
/// <param name="Lcom4">LCOM4 lack-of-cohesion: connected components formed by methods that share fields or call each other (1 is cohesive; higher suggests the type does unrelated jobs).</param>
/// <param name="Wmc">Weighted methods per class: the sum of the methods' cyclomatic complexities.</param>
public sealed record ClassMetrics(
    string Name,
    int Line,
    int MethodCount,
    int FieldCount,
    int Lcom4,
    int Wmc);

/// <summary>Aggregated metrics for a single source file.</summary>
/// <param name="File">Forward-slash path of the file, relative to the analysis base directory.</param>
/// <param name="Loc">Physical line count.</param>
/// <param name="Functions">Per-function metrics, including the synthetic <c>"global"</c> scope, sorted by descending cyclomatic complexity.</param>
/// <param name="Classes">Per-type metrics, sorted by declaration line.</param>
/// <param name="FanOut">Number of distinct modules/namespaces imported by the file.</param>
/// <param name="DuplicationRatio">Fraction (0..1) of meaningful lines that also appear as a duplicated block elsewhere in the analyzed set.</param>
/// <param name="CyclomaticSum">Sum of cyclomatic complexity across all functions.</param>
/// <param name="CognitiveSum">Sum of cognitive complexity across all functions.</param>
/// <param name="NPathMax">Largest NPath complexity of any single function.</param>
/// <param name="HalsteadVolume">Halstead volume computed over the whole file.</param>
/// <param name="MaintainabilityIndex">Maintainability index normalized to 0..100 (higher is more maintainable).</param>
/// <param name="ComplexityLevel">Classification of the most complex function: <c>"ok"</c>, <c>"warning"</c>, or <c>"error"</c>.</param>
/// <param name="MaxNestingDepth">Deepest level of nested control structures in any function.</param>
/// <param name="MaxParameterCount">Largest parameter list of any function (long parameter lists hurt readability and signal over-injection).</param>
/// <param name="HardCodedDependencies">Count of concrete dependencies constructed inline (<c>new Service()</c>) rather than injected; a higher count means harder-to-substitute collaborators.</param>
/// <param name="AmbientDependencies">Count of calls into ambient/impure APIs (clock, filesystem, network, environment, randomness, console) that cannot be mocked, making the code hard to isolate in tests.</param>
public sealed record FileMetrics(
    string File,
    int Loc,
    IReadOnlyList<FunctionMetrics> Functions,
    IReadOnlyList<ClassMetrics> Classes,
    int FanOut,
    double DuplicationRatio,
    int CyclomaticSum,
    int CognitiveSum,
    int NPathMax,
    double HalsteadVolume,
    double MaintainabilityIndex,
    string ComplexityLevel,
    int MaxNestingDepth,
    int MaxParameterCount,
    int HardCodedDependencies,
    int AmbientDependencies);
