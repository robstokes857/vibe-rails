using System.Diagnostics;
using VibeRails.Services.Tracing;

namespace VibeRails.Middleware;

/// <summary>
/// Records every HTTP request as a TraceEvent so it appears in the trace viewer.
/// Skips the SSE/WebSocket endpoints themselves to avoid noise.
/// </summary>
public sealed class TraceHttpMiddleware
{
    private static readonly HashSet<string> s_skipPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/trace/stream",
        "/api/v1/terminal/ws",
    };

    private readonly RequestDelegate _next;
    private readonly TraceEventBuffer _buffer;

    public TraceHttpMiddleware(RequestDelegate next, TraceEventBuffer buffer)
    {
        _next = next;
        _buffer = buffer;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // Skip static files and the streaming endpoints
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || s_skipPaths.Contains(path))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        var sw = Stopwatch.StartNew();

        await _next(context);

        sw.Stop();
        var status = context.Response.StatusCode;
        var summary = $"{method} {path} → {status}";
        var qs = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null;
        var detail = qs != null ? $"Query: {qs}" : null;

        _buffer.Add(TraceEvent.Create(
            Services.Tracing.TraceEventType.HttpRequest,
            "Http",
            summary,
            detail,
            sw.Elapsed.TotalMilliseconds));
    }
}
