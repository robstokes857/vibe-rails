using Microsoft.AspNetCore.Http;

namespace TokenSaver.Minify;

/// <summary>
/// Native Grok's cli-chat traffic is two body shapes behind one base URL (verified against
/// grok 1.0.5): grok-4.6 runs the Responses backend and POSTs <c>/responses</c>, while
/// grok-build — and any custom-models/API-mode call — POSTs <c>/chat/completions</c>. Each
/// inner transform declines by path, so exactly one attempts the rewrite per request.
///
/// Both run against a plan whose Codex/Zai slots hold <see cref="Pipeline.CompressionPlan.GrokAllowlist"/>:
/// Grok's wire tool names (<c>run_terminal_command</c> et al) match neither Codex's nor
/// OpenCode's, and the rewriters read their own provider's slot off the plan.
/// </summary>
internal sealed class CliChatBodyTransform : ILlmProxyBodyTransform
{
    public const string ProviderTag = "cli-chat";

    private readonly CodexBodyTransform _responses;
    private readonly ZaiBodyTransform _chatCompletions;

    public CliChatBodyTransform(Pipeline.CompressionPlan plan, ICompressionCaptureSink? captures = null)
    {
        var grokPlan = plan with
        {
            CodexAllowlist = plan.GrokAllowlist,
            ZaiAllowlist = plan.GrokAllowlist
        };
        _responses = new CodexBodyTransform(grokPlan, captures, ProviderTag);
        _chatCompletions = new ZaiBodyTransform(grokPlan, captures, ProviderTag);
    }

    public string Provider => ProviderTag;

    public async ValueTask<TransformedRequestBody?> TryTransformAsync(
        HttpRequest request, ILlmProxyEventSink events, CancellationToken cancellationToken) =>
        await _responses.TryTransformAsync(request, events, cancellationToken)
        ?? await _chatCompletions.TryTransformAsync(request, events, cancellationToken);
}
