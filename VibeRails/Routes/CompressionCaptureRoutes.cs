using TokenSaver.Minify;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Routes;

/// <summary>
/// The capture view's API: browse what the token saver actually did to each tool_result, and ask
/// what a different stage selection would have done to the same bytes.
///
/// The preview endpoint runs the REAL <see cref="CompressionPipeline"/> over the REAL stored
/// RawText. That is the whole reason captures store unescaped strings rather than wire bytes — a
/// re-implementation here would answer a question about itself instead of about the proxy, and
/// would drift the first time a stage changed.
/// </summary>
public static class CompressionCaptureRoutes
{
    private const int DefaultTake = 50;

    /// <summary>
    /// Ceiling on caller-supplied preview text. The captureId form is bounded by what the proxy
    /// actually stored; the text form is bounded only by the request body limit, and the pipeline
    /// allocates scratch proportional to its input. Well above any real tool_result.
    /// </summary>
    private const int MaxPreviewTextLength = 2_000_000;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/compression/captures", async (
            ICompressionCaptureStore store, int? take, int? skip, CancellationToken ct) =>
        {
            var summaries = await store.ListAsync(take ?? DefaultTake, skip ?? 0, ct);
            return Results.Ok(summaries.ConvertAll(s => new CompressionCaptureSummaryResponse(
                s.Id, s.CreatedUtc, s.Provider, s.ToolName, s.Command,
                s.CharsBefore, s.CharsAfter, s.Changed, s.RewriteAccepted)));
        }).WithName("GetCompressionCaptures");

        app.MapGet("/api/v1/compression/captures/{id:guid}", async (
            Guid id, ICompressionCaptureStore store, CancellationToken ct) =>
        {
            var capture = await store.GetAsync(id, ct);
            return capture is null
                ? Results.NotFound(new ErrorResponse($"No compression capture {id:D}."))
                : Results.Ok(new CompressionCaptureDetailResponse(
                    capture.Id, capture.CreatedUtc, capture.Provider, capture.ToolName,
                    capture.Command, capture.RawText, capture.CompressedText,
                    capture.CharsBefore, capture.CharsAfter, capture.Changed,
                    capture.RewriteAccepted,
                    ToTraceResponses(capture.Trace),
                    [.. capture.EnabledIds]));
        }).WithName("GetCompressionCapture");

        app.MapDelete("/api/v1/compression/captures", async (
            ICompressionCaptureStore store, CancellationToken ct) =>
        {
            return Results.Ok(new CompressionClearResponse(await store.ClearAsync(ct)));
        }).WithName("ClearCompressionCaptures");

        app.MapGet("/api/v1/compression/catalog", () =>
        {
            return Results.Ok(new CompressionCatalogResponse(
                [.. CompressionCatalog.Stages.Select(s => new CompressionStageResponse(
                    s.Id, s.Name, s.Summary, s.Kind.ToString(), s.OnByDefault, s.Order))],
                [.. CompressionCatalog.Scopes.Select(s => new CompressionScopeResponse(
                    s.Id, s.Name, s.Summary, s.OnByDefault, s.Warning))],
                [.. CompressionCatalog.DefaultSelection]));
        }).WithName("GetCompressionCatalog");

        app.MapPost("/api/v1/compression/preview", (
            CompressionPreviewRequest request, ICompressionCaptureStore store, CancellationToken ct) =>
            PreviewAsync(request, store, ct)).WithName("PreviewCompression");
    }

    /// <summary>
    /// The preview handler: two mutually exclusive request forms over one pipeline call.
    /// {captureId} replays a stored capture's RawText; {text, toolName, provider[, command]}
    /// (plan_1A A3) runs caller-supplied text, so an exchange-mined candidate string can be
    /// judged against the real pipeline without first being reproduced as live traffic.
    /// Extracted from the route lambda so the form validation is testable without a host.
    /// </summary>
    internal static async Task<IResult> PreviewAsync(
        CompressionPreviewRequest request, ICompressionCaptureStore store, CancellationToken ct)
    {
        // Whitespace-only counts as "not supplied": an empty string here would otherwise be read
        // as the text form, rejecting {captureId, text: ""} as "both" and running the pipeline
        // over nothing for {text: ""}.
        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            var text = request.Text;
            if (request.CaptureId is not null)
                return TypedResults.BadRequest(new ErrorResponse(
                    "Provide captureId or text, not both."));
            if (text.Length > MaxPreviewTextLength)
                return TypedResults.BadRequest(new ErrorResponse(
                    $"text is {text.Length} characters; the preview accepts at most "
                    + $"{MaxPreviewTextLength}."));
            if (string.IsNullOrEmpty(request.ToolName) || string.IsNullOrEmpty(request.Provider))
                return TypedResults.BadRequest(new ErrorResponse(
                    "The text form requires toolName and provider (capture wire keys, e.g. "
                    + "\"anthropic\", \"openai\", \"zai\", \"xai\")."));

            var (textOutput, textTrace, textScopeAllowed) = RunPipeline(
                text, request.Command, request.Provider, request.ToolName, request.EnabledIds);
            return TypedResults.Ok(new CompressionPreviewResponse(
                textOutput, ToTraceResponses(textTrace), text.Length, textOutput.Length,
                textScopeAllowed));
        }

        if (request.CaptureId is not { } captureId)
            return TypedResults.BadRequest(new ErrorResponse(
                "Provide captureId (replay a stored capture) or text (preview supplied text)."));

        var capture = await store.GetAsync(captureId, ct);
        if (capture is null)
            return TypedResults.NotFound(new ErrorResponse($"No compression capture {captureId:D}."));

        var (output, trace, scopeAllowed) = RunPipeline(
            capture.RawText,
            capture.Command,
            capture.Provider,
            capture.ToolName,
            request.EnabledIds);
        return TypedResults.Ok(new CompressionPreviewResponse(
            output, ToTraceResponses(trace), capture.RawText.Length, output.Length,
            scopeAllowed));
    }

    /// <summary>
    /// Re-runs one capture's RawText through the pipeline under an arbitrary stage selection.
    ///
    /// Synchronous and separate from the handler on purpose: <see cref="CompressionPipeline.Run"/>
    /// returns a span aliasing the scratch buffers, which cannot be held across an await — so the
    /// string is materialized before control can ever return to the async handler.
    /// </summary>
    internal static (string Output, List<StageTrace> Trace, bool ScopeAllowed) RunPipeline(
        string rawText,
        string? command,
        string provider,
        string toolName,
        IReadOnlyCollection<string>? enabledIds)
    {
        // Passed through untouched rather than defaulted here: Resolve treats null as "never
        // configured" (→ catalog defaults) and empty as a real "everything off", and collapsing
        // that distinction would make "preview with nothing enabled" silently preview the defaults.
        var plan = CompressionCatalog.Resolve(enabledIds);

        var allowlist = provider switch
        {
            "anthropic" => plan.AnthropicAllowlist,
            "openai" => plan.CodexAllowlist,
            "zai" or "xai" => plan.ZaiAllowlist,
            _ => [],
        };
        var scopeAllowed = allowlist.Any(
            allowed => string.Equals(allowed, toolName, StringComparison.Ordinal));
        if (!scopeAllowed)
        {
            // Scopes are execution gates, not display-only metadata. The real proxy would never
            // invoke the pipeline for this tool under this selection, so a faithful what-if must
            // return the raw payload rather than showing a transform that could not happen.
            return (rawText, [], false);
        }

        // Keyed off the recorded command, never off sniffing the output — the shape filters' whole
        // safety argument is that they only fire on a command they recognise.
        var shape = CommandShapes.Classify(command);

        var trace = new List<StageTrace>();
        var minifyStats = default(MinifyStats);
        var condenseStats = default(CondenseStats);
        using var scratch = new PipelineScratch(rawText.Length);
        var output = CompressionPipeline.Run(
            rawText, plan, shape, scratch, out _, ref minifyStats, ref condenseStats, trace);
        return (output.ToString(), trace, true);
    }

    private static List<CompressionStageTraceResponse> ToTraceResponses(IReadOnlyList<StageTrace> trace) =>
        [.. trace.Select(t => new CompressionStageTraceResponse(
            t.StageId, t.Outcome.ToString(), t.CharsRemoved))];
}
