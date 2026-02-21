namespace VibeRails.Services.Tracing;

public enum TraceEventType
{
    TerminalInput,
    TerminalOutput,
    McpToolCall,
    McpToolResult,
    RuleValidation,
    LogEntry,
    SessionLifecycle,
    Idle,
    Resize,
    HttpRequest,
    TerminalLaunch
}

public record TraceEvent(
    string Id,
    DateTimeOffset Timestamp,
    TraceEventType Type,
    string Source,
    string Summary,
    string? Detail = null,
    double? DurationMs = null)
{
    public static TraceEvent Create(TraceEventType type, string source, string summary, string? detail = null, double? durationMs = null)
        => new(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow, type, source, summary, detail, durationMs);
}
