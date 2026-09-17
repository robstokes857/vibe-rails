using TokenSaver.Minify;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using VibeRails.DTOs;

namespace VibeRails.Routes;

/// <summary>
/// The token saver's what-if API: the stage catalog, and a preview that runs the REAL
/// <see cref="CompressionPipeline"/> over caller-supplied text. A re-implementation here would
/// answer a question about itself instead of about the proxy, and would drift the first time a
/// stage changed.
///
/// The per-tool_result capture table this used to browse (GET/DELETE /api/v1/compression/captures,
/// the {captureId} preview form) was retired on 2026-09-17: its re-sight UPDATE scanned the table
/// under state.db's writer lock, hundreds of times per request, and the always-on exchange log
/// already holds the same bytes.
/// </summary>
public static class CompressionRoutes
{
    /// <summary>
    /// Ceiling on caller-supplied preview text. It is bounded only by the request body limit, and
    /// the pipeline allocates scratch proportional to its input. Well above any real tool_result.
    /// </summary>
    private const int MaxPreviewTextLength = 2_000_000;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/compression/catalog", () =>
        {
            return Results.Ok(new CompressionCatalogResponse(
                [.. CompressionCatalog.Stages.Select(s => new CompressionStageResponse(
                    s.Id, s.Name, s.Summary, s.Kind.ToString(), s.OnByDefault, s.Order))],
                [.. CompressionCatalog.Scopes.Select(s => new CompressionScopeResponse(
                    s.Id, s.Name, s.Summary, s.OnByDefault, s.Warning))],
                [.. CompressionCatalog.DefaultSelection]));
        }).WithName("GetCompressionCatalog");

        app.MapPost("/api/v1/compression/preview", (CompressionPreviewRequest request) =>
            Preview(request)).WithName("PreviewCompression");
    }

    /// <summary>
    /// The preview handler: {text, toolName, provider[, command]} (plan_1A A3) runs caller-supplied
    /// text, so an exchange-mined candidate string can be judged against the real pipeline without
    /// first being reproduced as live traffic. Extracted from the route lambda so the form
    /// validation is testable without a host.
    /// </summary>
    internal static IResult Preview(CompressionPreviewRequest request)
    {
        // Whitespace-only counts as "not supplied": running the pipeline over nothing would
        // otherwise look like a successful preview.
        if (string.IsNullOrWhiteSpace(request.Text))
            return TypedResults.BadRequest(new ErrorResponse(
                "Provide text to preview (with toolName and provider)."));

        var text = request.Text;
        if (text.Length > MaxPreviewTextLength)
            return TypedResults.BadRequest(new ErrorResponse(
                $"text is {text.Length} characters; the preview accepts at most "
                + $"{MaxPreviewTextLength}."));
        if (string.IsNullOrEmpty(request.ToolName) || string.IsNullOrEmpty(request.Provider))
            return TypedResults.BadRequest(new ErrorResponse(
                "The preview requires toolName and provider (proxy wire keys, e.g. "
                + "\"anthropic\", \"openai\", \"zai\", \"xai\")."));

        var (output, trace, scopeAllowed) = RunPipeline(
            text, request.Command, request.Provider, request.ToolName, request.EnabledIds);
        return TypedResults.Ok(new CompressionPreviewResponse(
            output, ToTraceResponses(trace), text.Length, output.Length, scopeAllowed));
    }

    /// <summary>
    /// Runs one text through the pipeline under an arbitrary stage selection.
    ///
    /// Synchronous and separate from the handler on purpose: <see cref="CompressionPipeline.Run"/>
    /// returns a span aliasing the scratch buffers, which cannot be held across an await — so the
    /// string is materialized before control can ever return to an async caller.
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

        // Keyed off the supplied command, never off sniffing the output — the shape filters' whole
        // safety argument is that they only fire on a command they recognise.
        var shape = CommandShapes.Classify(command);
        var readsFileContents = CommandShapes.ReadsFileContents(command);

        var trace = new List<StageTrace>();
        var minifyStats = default(MinifyStats);
        var condenseStats = default(CondenseStats);
        using var scratch = new PipelineScratch(rawText.Length);
        var output = CompressionPipeline.Run(
            rawText, plan, shape, readsFileContents, scratch, out _, ref minifyStats,
            ref condenseStats, trace);
        return (output.ToString(), trace, true);
    }

    private static List<CompressionStageTraceResponse> ToTraceResponses(IReadOnlyList<StageTrace> trace) =>
        [.. trace.Select(t => new CompressionStageTraceResponse(
            t.StageId, t.Outcome.ToString(), t.CharsRemoved))];
}
