using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Local reverse proxy for OpenCode's xai (Grok) provider: catches
/// <c>/llm/xai/{**rest}</c> and forwards to <c>https://api.x.ai/{rest}</c>. OpenCode points here via
/// the <c>OPENCODE_CONFIG_CONTENT</c> env var (see <see cref="LlmProxyXaiConfig"/>).
///
/// This is the route the token saver rides for Grok models routed through OpenCode: when enabled,
/// qualifying <c>/chat/completions</c> bodies are rewritten by <see cref="ZaiBodyTransform"/>
/// (tool-message content minification only; same Chat Completions shape as Z.AI); everything else
/// — and every failure path — is a pure passthrough. The shared streaming relay, header filtering,
/// auth gate, and disconnect handling all live in <see cref="LlmProxyRelay"/>.
/// </summary>
public static class LlmXaiProxyRoutes
{
    private const string UpstreamHost = LlmProxyXaiConfig.UpstreamHost;
    private const string PathPrefix = LlmProxyXaiConfig.XaiProxyPath + "/";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map(LlmProxyXaiConfig.XaiProxyPath + "/{**rest}", static async context =>
        {
            var settings = context.RequestServices
                .GetRequiredService<ILlmProxySettingsService>()
                .GetSettings();
            if (!settings.OpenCodeLlmProxyEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var plan = settings.ResolvedPlan;
            var saverHasWork = !plan.IsNoOp && plan.ZaiAllowlist.Count > 0;
            var captureSink = settings.TokenSaverCaptureEnabled
                ? context.RequestServices.GetService<ICompressionCaptureSink>()
                : null;
            var transform = settings.OpenCodeTokenSaverEnabled && saverHasWork
                ? new ZaiBodyTransform(plan, captureSink, provider: "xai")
                : null;
            var exchangeSink = context.RequestServices.GetRequiredService<ILlmProxyExchangeSink>();

            var target = LlmProxyRelay.BuildTarget(context.Request, UpstreamHost, PathPrefix);
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                context.RequestServices.GetRequiredService<ILlmProxyEventSink>(),
                target,
                "xai",
                "OpenCode proxy",
                bodyTransform: transform,
                context.RequestAborted,
                exchangeSink);
        }).WithName("LlmXaiProxy");
    }
}
