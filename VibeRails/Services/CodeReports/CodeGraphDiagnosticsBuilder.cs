using VibeRails.DTOs;

namespace VibeRails.Services.CodeReports;

// Per-request counts only; no source text, paths or persistent state.
internal sealed class CodeGraphDiagnosticsBuilder
{
    private readonly Dictionary<string, (int Count, string Detail)> omissions = new(StringComparer.Ordinal);
    public int SupportedFiles { get; set; }
    public int ExcludedDependencyFiles { get; set; }
    public int ExcludedBuildOutputFiles { get; set; }
    public bool IncludesDependencies { get; set; }

    public void Add(string code, int count, string detail)
    {
        if (count <= 0) return;
        omissions.TryGetValue(code, out var previous);
        omissions[code] = (previous.Count + count, detail);
    }

    public CodeGraphDiagnostics Snapshot() => new(SupportedFiles, ExcludedDependencyFiles,
        ExcludedBuildOutputFiles, IncludesDependencies,
        omissions.Select(pair => new CodeGraphOmission(pair.Key, pair.Value.Count, pair.Value.Detail)).ToArray());
}
