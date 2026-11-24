using MintLint;

namespace VibeRails.Services.GitPreflight;

/// <summary>One measurement past its warning or critical limit, as <see cref="CodeQualityFindings"/> reports it.</summary>
internal sealed record QualityProblem(string Metric, string Label, string Level, string Value)
{
    /// <summary>The function or type it was measured in, "top-level code", or null for a whole-file measurement.</summary>
    public string? Source { get; init; }
    public int? Line { get; init; }
    /// <summary>"critical at 20" or "warning above 10".</summary>
    public string Limit { get; init; } = "";
    /// <summary>What to change, in a sentence or two.</summary>
    public string? Fix { get; init; }
    /// <summary>The value before the change, when the function already existed and the change made it worse.</summary>
    public string? Was { get; init; }
}

/// <summary>One analyzed file: its rating, health (100 minus the concern score) and problems, worst first.</summary>
internal sealed record QualityFile(string Path, string Rating, double Health, IReadOnlyList<QualityProblem> Problems)
{
    public char Grade { get; init; } = 'A';
    /// <summary>The lines of code that weigh this file in the overall grade.</summary>
    public double Lines { get; init; } = 1;
    /// <summary>The measurement nearest its limit when nothing is past one.</summary>
    public string? Closest { get; init; }
    /// <summary>The check's own definition of a finding: a file graded D or F, rated Needs work or At risk.</summary>
    public bool NeedsWork => Rating is "NeedsWork" or "AtRisk";
    public string RatingLabel => Rating switch { "NeedsWork" => "Needs work", "AtRisk" => "At risk", "" => "Not rated", _ => Rating };
    public string HealthText => QualityText.Format(Health, 1);
    public int CriticalCount => Problems.Count(problem => problem.Level == MetricFinding.CriticalLevel);
    /// <summary>"F · health 40/100": the grade first, because that is what a minimum is set in.</summary>
    public string GradeText => $"{Grade} · health {HealthText}/100";
}
