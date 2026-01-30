using System.Text.Json.Serialization;

namespace MCP_Server.Models;

/// <summary>
/// Represents a mapping between user terminology and a code file path.
/// </summary>
internal class UserTermEntry
{
    public string Id { get; set; } = string.Empty;
    public string UserTerm { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Represents a stored conversation entry for context retrieval.
/// </summary>
internal class ConversationHistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ProjectPath { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// JSON context for AOT-compatible serialization of vector storage models.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UserTermEntry))]
[JsonSerializable(typeof(ConversationHistoryEntry))]
internal partial class VectorJsonContext : JsonSerializerContext
{
}
