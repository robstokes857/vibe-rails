namespace VibeRails.Services.GitPreflight;

/// <summary>
/// What each Code quality measurement is called, what it is measured on, how to fix it and how its
/// values are written. The labels and fixes repeat the Quality page's <c>code-report/findings.js</c>
/// GUIDE table; <c>CodeQualityFindingsTests</c> pins both to that file, so change them together.
/// </summary>
internal static class CodeQualityGuide
{
    internal const string TopLevelCode = "top-level code";
    private const double NpathCap = 1_000_000_000;

    private static readonly Dictionary<string, QualityGuideEntry> Entries = new(StringComparer.Ordinal)
    {
        ["cyclomatic_complexity"] = new("Cyclomatic complexity", "function",
            "Split it into smaller functions that each do one job. Return early instead of nesting else blocks, and replace long if/else or switch chains with a lookup table."),
        ["cognitive_complexity"] = new("Cognitive complexity", "function",
            "Flatten it: use guard clauses, move nested loop and branch bodies into named helpers, and avoid mixing && and || in one condition."),
        ["npath_complexity"] = new("NPath execution paths", "function",
            "Break the function into steps that each return a result, and remove flags that change what later code in the same function does."),
        ["nesting_depth"] = new("Nesting depth", "function",
            "Invert conditions and return early, and move the innermost block into its own function."),
        ["parameter_count"] = new("Parameters per function", "function",
            "Group related values into one options object or record, or split the function so each part takes only what it needs."),
        ["halstead_difficulty"] = new("Halstead difficulty", "function",
            "Name intermediate results, pull repeated expressions into variables or helpers, and split long expressions into steps."),
        ["maintainability_index"] = new("Maintainability index", "file",
            "It rises when the code gets shorter and its most complex functions get simpler, so fix the other problems in this file first.") { Composite = true },
        ["lines_of_code"] = new("Lines of code", "file",
            "Move each separate responsibility into its own file or module."),
        ["method_count"] = new("Methods per type", "class",
            "Split it by responsibility: move each group of related methods into its own type."),
        ["field_count"] = new("Fields per type", "class",
            "Group fields that change together into small types, or split the type."),
        ["lack_of_cohesion"] = new("Lack of cohesion (LCOM4)", "class",
            "Split the type along those groups: each group of methods and the fields it uses can become its own type."),
        ["fan_out"] = new("Fan-out (modules imported)", "file",
            "Split the file so each part imports only what it uses, or put the code that coordinates many modules behind one smaller interface."),
        ["duplication"] = new("Duplicated lines", "file",
            "Extract the repeated block into one shared function and call it from each place.") { Percent = true },
        ["hard_coded_dependencies"] = new("Hard-coded dependencies", "file",
            "Pass them in through the constructor or parameters instead of creating them inside."),
        ["ambient_dependencies"] = new("Ambient dependencies", "file",
            "Wrap them behind small interfaces that you pass in, so a test can supply fixed values.")
    };

    /// <summary>The labels of findings.js's GUIDE table, by metric.</summary>
    internal static IReadOnlyDictionary<string, string> Labels { get; } =
        Entries.ToDictionary(pair => pair.Key, pair => pair.Value.Label, StringComparer.Ordinal);

    /// <summary>The fixes of findings.js's GUIDE table, by metric.</summary>
    internal static IReadOnlyDictionary<string, string> Fixes { get; } =
        Entries.ToDictionary(pair => pair.Key, pair => pair.Value.Fix, StringComparer.Ordinal);

    /// <summary>The entry for a metric, or null for one this table does not know.</summary>
    internal static QualityGuideEntry? For(string metric) => Entries.GetValueOrDefault(metric);

    /// <summary>The metric's label; an unknown metric gets its name in words.</summary>
    internal static string Label(string metric)
    {
        if (For(metric) is { } entry) return entry.Label;
        var words = metric.Replace('_', ' ');
        return words.Length == 0 ? "Measurement" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>The function or type a measurement belongs to: its name, top-level code for a function
    /// measurement without one, or null for the whole file.</summary>
    internal static string? Source(string metric, string? source, string? scope = null)
    {
        if (!string.IsNullOrWhiteSpace(source)) return source.Trim();
        return (scope ?? For(metric)?.Scope) == "function" ? TopLevelCode : null;
    }

    /// <summary>A value as it is written: a percentage, the NPath cap, or a number with at most one decimal.</summary>
    internal static string Measure(string metric, double value)
    {
        if (For(metric)?.Percent == true) return $"{QualityText.Format(value * 100, 1)}%";
        if (metric == "npath_complexity" && value >= NpathCap) return "1B (cap)";
        return QualityText.Format(value, value >= 100 ? 0 : 1);
    }

    /// <summary>"critical at 20" for a critical value, otherwise "warning above 10" (or "below" when higher is better).</summary>
    internal static string Limit(string metric, MintLint.MetricThreshold limits, bool critical) => critical
        ? $"critical at {Measure(metric, limits.Critical)}"
        : $"warning {(limits.HigherIsBetter ? "below" : "above")} {Measure(metric, limits.Warn)}";
}

/// <summary>One row of the guide: a measurement's label, what it is measured on and how to fix it.</summary>
/// <param name="Scope">What a source and line name: <c>function</c>, <c>class</c> or <c>file</c>.</param>
internal sealed record QualityGuideEntry(string Label, string Scope, string Fix)
{
    /// <summary>A composite only improves when the measurements it is built from do, so it lists last.</summary>
    public bool Composite { get; init; }
    /// <summary>The value is a fraction written as a percentage.</summary>
    public bool Percent { get; init; }
}
