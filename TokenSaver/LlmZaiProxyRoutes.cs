using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Local reverse proxy for OpenCode's zai/Z.AI (GLM) provider: catches
/// <c>/llm/zai/{**rest}</c> and forwards to <c>https://api.z.ai/{rest}</c>. OpenCode points here via
/// the <c>OPENCODE_CONFIG_CONTENT</c> env var (see <see cref="LlmProxyZaiConfig"/>).
///
/// This is the route the token saver rides for GLM models routed through OpenCode: when enabled,
/// qualifying <c>/chat/completions</c> bodies are rewritten by <see cref="ZaiBodyTransform"/>
/// (tool-message content minification only); everything else — and every failure path — is a pure
/// passthrough. The shared streaming relay, header filtering, auth gate, and disconnect handling
/// all live in <see cref="LlmProxyRelay"/>.
/// </summary>
public static class LlmZaiProxyRoutes
{
    private const string UpstreamHost = LlmProxyZaiConfig.UpstreamHost;
    private const string PathPrefix = LlmProxyZaiConfig.ZaiProxyPath + "/";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map(LlmProxyZaiConfig.ZaiProxyPath + "/{**rest}", static async context =>
        {
            var settings = context.RequestServices
                .GetRequiredService<ILlmProxySettingsService>()
                .GetSettings();
            if (!settings.OpenCodeLlmProxyEnabled)
            {
                // Feature is off: behave as if the endpoint doesn't exist rather than leaving an
                // always-on relay to api.z.ai.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // The saver's kill switch sits apart from the proxy toggle so stages can be bisected or
            // disabled without tearing down the relay itself.
            var plan = settings.ResolvedPlan;
            var saverHasWork = !plan.IsNoOp && plan.ZaiAllowlist.Count > 0;
            var captureSink = settings.TokenSaverCaptureEnabled
                ? context.RequestServices.GetService<ICompressionCaptureSink>()
                : null;
            var transform = settings.OpenCodeTokenSaverEnabled && saverHasWork
                ? new ZaiBodyTransform(plan, captureSink)
                : null;
            // Exchange logging is an invariant of using the proxy, not a setting.
            var exchangeSink = context.RequestServices.GetRequiredService<ILlmProxyExchangeSink>();

            var target = LlmProxyRelay.BuildTarget(context.Request, UpstreamHost, PathPrefix);
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                context.RequestServices.GetRequiredService<ILlmProxyEventSink>(),
                target,
                "zai",
                "OpenCode proxy",
                bodyTransform: transform,
                context.RequestAborted,
                exchangeSink);
        }).WithName("LlmZaiProxy");
    }
}
