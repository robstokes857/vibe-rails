namespace VibeRails.DTOs;

/// <summary>
/// Small result returned after the repository has streamed an envelope. Large log/input payloads
/// are never materialized as an object graph.
/// </summary>
public sealed record SessionDataExportDescriptor(
    int SchemaVersion,
    string Kind,
    Guid SourceId);

/// <summary>
/// One session the drain job may attempt, with the number of attempts already recorded against it.
/// The count travels with the selection so a failure can be persisted as a single UPDATE.
/// </summary>
public sealed record UnexportedSessionRef(string SessionId, int Attempts);
