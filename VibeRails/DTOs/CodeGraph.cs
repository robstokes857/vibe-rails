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
    DateTime CapturedUtc, bool Truncated, int FileCount, string Description);
