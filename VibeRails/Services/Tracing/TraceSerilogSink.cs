using Serilog.Core;
using Serilog.Events;

namespace VibeRails.Services.Tracing;

/// <summary>
/// Serilog sink that forwards all log events to the trace buffer.
/// This auto-captures existing logging from McpClientService, RuleValidationService,
/// and every other service — zero code changes needed in business logic.
/// </summary>
public sealed class TraceSerilogSink : ILogEventSink
{
    private readonly TraceEventBuffer _buffer;

    public TraceSerilogSink(TraceEventBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Emit(LogEvent logEvent)
    {
        var source = "Serilog";
        if (logEvent.Properties.TryGetValue("SourceContext", out var ctx))
        {
            // Extract just the class name from fully qualified name
            var full = ctx.ToString().Trim('"');
            var lastDot = full.LastIndexOf('.');
            source = lastDot >= 0 ? full[(lastDot + 1)..] : full;
        }

        var level = logEvent.Level switch
        {
            LogEventLevel.Error or LogEventLevel.Fatal => "ERR",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Debug or LogEventLevel.Verbose => "DBG",
            _ => "INF"
        };

        var summary = $"[{level}] {source}: {logEvent.RenderMessage()}";

        string? detail = null;
        if (logEvent.Exception != null)
        {
            detail = logEvent.Exception.ToString();
        }

        _buffer.Add(TraceEvent.Create(
            TraceEventType.LogEntry,
            source,
            summary,
            detail));
    }
}
