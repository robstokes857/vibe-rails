using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Local reverse proxy for Claude Code: catches <c>/llm/anthropic/{**rest}</c> and forwards to
/// <c>https://api.anthropic.com/{rest}</c>. Claude points here via <c>ANTHROPIC_BASE_URL</c>.
///
/// This is the route the token saver rides: when enabled, qualifying <c>/v1/messages</c> bodies
/// are rewritten by <see cref="AnthropicBodyTransform"/> (Bash tool_result minification only);
/// everything else — and every failure path — is a pure passthrough. The shared streaming relay,
/// header filtering, auth gate, and disconnect handling all live in <see cref="LlmProxyRelay"/>.
/// Handlers are plain <see cref="RequestDelegate"/>s for AOT safety — see
/// <see cref="LlmProxyRoutes"/> for why.
/// </summary>
public static class LlmAnthropicProxyRoutes
{
    private const string UpstreamHost = LlmProxyClaudeConfig.UpstreamHost;
    private const string PathPrefix = LlmProxyClaudeConfig.AnthropicProxyPath + "/";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map(LlmProxyClaudeConfig.AnthropicProxyPath + "/{**rest}", static async context =>
        {
            var settings = context.RequestServices
                .GetRequiredService<ILlmProxySettingsService>()
                .GetSettings();
            if (!settings.ClaudeLlmProxyEnabled)
            {
                // Feature is off: behave as if the endpoint doesn't exist rather than leaving an
                // always-on relay to api.anthropic.com.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // The saver's kill switch (and per-transform flags) sit apart from the proxy toggle so
            // transforms can be bisected or disabled without tearing down the relay itself. Both
            // stages must be no-op to skip the transform — minify flags alone would leave a
            // condense-only configuration silently dead.
            var transform = settings.ClaudeTokenSaverEnabled
                    && !(settings.ClaudeTokenSaverFlags.IsNoOp && settings.ClaudeTokenSaverCondense.IsNoOp)
                ? new AnthropicBodyTransform(
                    settings.ClaudeTokenSaverFlags,
                    settings.ClaudeTokenSaverCondense,
                    settings.ClaudeTokenSaverAllowlist)
                : null;

            var target = LlmProxyRelay.BuildTarget(context.Request, UpstreamHost, PathPrefix);
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                context.RequestServices.GetRequiredService<ILlmProxyEventSink>(),
                target,
                "Claude proxy",
                transform,
                context.RequestAborted);
        }).WithName("LlmAnthropicProxy");
    }
}
