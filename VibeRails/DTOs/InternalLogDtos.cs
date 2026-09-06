namespace VibeRails.DTOs;

/// <summary>A deliberately small diagnostic event; callers must omit secrets and payload bodies.</summary>
public sealed record InternalLogEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string Feature,
    string Level,
    string EventName,
    string Message,
    string? OperationId = null,
    string? Subject = null,
    string? Status = null,
    string Source = "features",
    string? SourceFile = null);

/// <summary>
/// A bounded page of retained events plus logger health. <see cref="DroppedCount"/> and
/// <see cref="WriteFailures"/> count individual events lost over the process lifetime;
/// <see cref="ReadFailures"/> and <see cref="Truncated"/> describe only the snapshot this page was
/// served from, so repeated refreshes over the same damaged file report a stable number.
/// </summary>
public sealed record InternalLogResponse(
    IReadOnlyList<InternalLogEntry> Entries,
    IReadOnlyList<string> Features,
    bool HasMore,
    long DroppedCount,
    long WriteFailures,
    long ReadFailures = 0,
    bool Truncated = false);
