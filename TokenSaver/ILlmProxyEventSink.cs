using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Everything the proxy reports back to the host app: UI activity pings, per-request byte savings,
/// and debug diagnostics. Implementations must never block — the relay calls these on the LLM hot
/// path, so any I/O (event bus fan-out aside) belongs off-thread on the host side. None of these
/// calls ever carries secrets, request/response bodies, or query strings.
/// </summary>
public interface ILlmProxyEventSink
{
    /// <summary>
    /// Display-only ping that lights the proxy indicator: method, query-free upstream path, and
    /// status only. <paramref name="bytesSaved"/> rides along when the request was minified; the
    /// host attaches its own running total when publishing, so the UI tally updates without a
    /// second event.
    /// </summary>
    void ProxyActivity(
        string source,
        string? label,
        string? target,
        string? status,
        long? bytesSaved = null);

    /// <summary>
    /// One request's minification outcome. Must return immediately: the host persists the tally
    /// fire-and-forget and swallows failures — savings telemetry is never worth a slower or
    /// riskier relay.
    /// </summary>
    void SavingsMeasured(LlmProxySavingsReport report);

    /// <summary>Debug-level diagnostics (e.g. mid-stream disconnects); host routes to its logger.</summary>
    void Diagnostic(string source, string message, Exception? exception = null);
}

/// <summary>
/// One request's minification result. Byte counts are wire bytes of the request body before/after
/// the rewrite; <paramref name="Transforms"/> carries the char-domain per-transform counters for
/// the diagnostics log line.
/// </summary>
public sealed record LlmProxySavingsReport(
    string Provider,
    int BytesBefore,
    int BytesAfter,
    int ToolResultsMinified,
    int ToolResultsSeen,
    MinifyStats Transforms,
    CondenseStats Condensed = default)
{
    public int BytesSaved => BytesBefore - BytesAfter;
}
