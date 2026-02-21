using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Tracing;

namespace VibeRails.Routes;

public static class TraceRoutes
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = AppJsonSerializerContext.Default
    };

    public static void Map(WebApplication app)
    {
        // SSE stream — real-time trace events
        app.MapGet("/api/v1/trace/stream", async (
            TraceEventBuffer buffer,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            // Send catch-up events
            var recent = buffer.GetRecent(50);
            foreach (var evt in recent)
            {
                await WriteEvent(context.Response, evt, cancellationToken);
            }
            await context.Response.Body.FlushAsync(cancellationToken);

            // Stream new events via SSE
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetResult());

            void OnEvent(TraceEvent evt)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WriteEvent(context.Response, evt, cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }, cancellationToken);
            }

            buffer.OnEvent += OnEvent;
            try
            {
                await tcs.Task;
            }
            finally
            {
                buffer.OnEvent -= OnEvent;
            }
        }).WithName("TraceStream").ExcludeFromDescription();

        // REST endpoint — query recent events
        app.MapGet("/api/v1/trace/events", (TraceEventBuffer buffer, int? count) =>
        {
            var events = buffer.GetRecent(count ?? 100);
            return Results.Ok(events);
        }).WithName("GetTraceEvents");
    }

    private static async Task WriteEvent(HttpResponse response, TraceEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, AppJsonSerializerContext.Default.TraceEvent);
        await response.WriteAsync($"event: trace\ndata: {json}\n\n", ct);
    }
}
