namespace VibeRails.DTOs;

/// <summary>
/// Small result returned after the repository has streamed an envelope. Large log/input payloads
/// are never materialized as an object graph.
/// </summary>
/// <param name="ProxyCoverage">
/// The proxyCoverage.status actually written into this envelope ("included", "empty" or
/// "unavailable"), or null when it is not known -- notably for a frozen spool rediscovered from a
/// previous process. Retention treats anything other than "included" as no proof that the
/// session's proxy exchanges were backed up, and therefore never prunes them.
/// </param>
/// <param name="ProxyMaxRowId">
/// The largest proxy rowid inside an "included" snapshot, or null. Together with
/// <paramref name="ProxyCoverage"/> it bounds what retention may prune: an exchange written after
/// the snapshot has a larger rowid and was never uploaded, whatever its CreatedUTC says.
/// </param>
public sealed record SessionDataExportDescriptor(
    int SchemaVersion,
    string Kind,
    Guid SourceId,
    string? ProxyCoverage = null,
    long? ProxyMaxRowId = null);

/// <summary>
/// One session the drain job may attempt, with the number of attempts already recorded against it.
/// The count travels with the selection so a failure can be persisted as a single UPDATE.
/// </summary>
public sealed record UnexportedSessionRef(string SessionId, int Attempts);
