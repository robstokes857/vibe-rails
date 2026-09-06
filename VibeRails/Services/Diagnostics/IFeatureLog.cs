using VibeRails.DTOs;

namespace VibeRails.Services.Diagnostics;

/// <summary>
/// Opt-in local feature diagnostics. Write only small metadata, never API keys, headers,
/// transcript text, uploaded bodies, or exception messages containing remote response bodies.
/// The production implementation queues without waiting for the disk or other callers.
/// </summary>
public interface IFeatureLog
{
    void Write(string feature, string eventName, string message, string? operationId = null,
        string? subject = null, string? status = null, LogLevel level = LogLevel.Information);
}

/// <summary>On-demand access to retained diagnostics; no polling occurs inside the logger.</summary>
public interface IFeatureLogReader
{
    Task<InternalLogResponse> ReadAsync(FeatureLogQuery query, bool uploadsOnly = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Filters are combined, case insensitive, and applied before pagination.</summary>
public sealed record FeatureLogQuery(string? Feature = null, string? Level = null,
    string? Status = null, string? Search = null, string? OperationId = null,
    int Offset = 0, int Limit = 100);

/// <summary>Used by process hosts that do not own feature logging, and lightweight service tests.</summary>
public sealed class NullFeatureLog : IFeatureLog
{
    public static NullFeatureLog Instance { get; } = new();

    private NullFeatureLog() { }

    public void Write(string feature, string eventName, string message, string? operationId = null,
        string? subject = null, string? status = null, LogLevel level = LogLevel.Information) { }
}
