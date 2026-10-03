using System.Text.Json.Serialization;

namespace VibeRails.DTOs;

/// <summary>Optional report paths to prioritize within the bounded repository snapshot.</summary>
public sealed record CodeGraphRequest(string[]? Files = null);
/// <summary>Display identity for the server-resolved repository.</summary>
public sealed record CodeGraphRepository(string Name);
/// <summary>Atlas entity. Optional strings are absent on the wire rather than null.</summary>
public sealed record CodeGraphNode(string Id, string Name, string Kind, string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Language = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null);
/// <summary>A structural connection or lexical reference, with its source evidence when available.</summary>
public sealed record CodeGraphEdge(string Id, string Source, string Target, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Evidence = null);
/// <summary>Bounded current working-tree graph; truncation never implies a complete dependency analysis.</summary>
public sealed record CodeGraphResponse(string SchemaVersion, CodeGraphRepository Repository,
    IReadOnlyList<CodeGraphNode> Nodes, IReadOnlyList<CodeGraphEdge> Edges,
    DateTime CapturedUtc, bool Truncated, int FileCount, string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodeGraphDiagnostics? Diagnostics = null);

/// <summary>Counts explain intentional filtering separately from bounded omissions.</summary>
public sealed record CodeGraphDiagnostics(int SupportedFiles, int ExcludedDependencyFiles,
    int ExcludedBuildOutputFiles, bool IncludesDependencies, IReadOnlyList<CodeGraphOmission> Omissions);
/// <summary>A bounded omission count with its unit and explanation.</summary>
public sealed record CodeGraphOmission(string Code, int Count, string Detail);

/// <summary>Working-tree changes against HEAD for the Code quality card: statuses and counts, never the diffs themselves.</summary>
public sealed record WorkingTreeChangesResponse(int Count, int Additions, int Deletions, bool Truncated,
    IReadOnlyList<WorkingTreeChangeFile> Files, DateTime CapturedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Head = null);
/// <summary>One changed path. Counts are absent for binary files and for files git could not count.</summary>
public sealed record WorkingTreeChangeFile(string Path, string Status, bool Staged, bool Unstaged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Additions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Deletions,
    bool Binary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginalPath = null);
/// <summary>Before/after text of one changed file in the shape the shared Monaco diff viewer consumes.</summary>
public sealed record WorkingTreeDiffResponse(string FileName, string Language, string Status,
    string OriginalContent, string ModifiedContent, bool Binary, bool Truncated);
