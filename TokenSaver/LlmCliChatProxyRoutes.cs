using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Local reverse proxy for the native Grok Build CLI: catches
/// <c>/llm/cli-chat/{**rest}</c> and forwards to
/// <c>https://cli-chat-proxy.grok.com/{rest}</c>. Grok points here via
/// <c>GROK_CLI_CHAT_PROXY_BASE_URL</c> (see <see cref="LlmProxyGrokConfig"/>).
///
/// Grok's own <c>Authorization</c>, <c>X-XAI-Token-Auth</c>, and
/// <c>x-grok-model-override</c> are forwarded unchanged.
/// <c>viberails_session</c> / <c>viberails_tab</c> authenticate the local hop
/// and are stripped before the upstream request.
///
/// This is not <c>/llm/grok</c> and not a second listener.
/// </summary>
public static class LlmCliChatProxyRoutes
{
    private const string PathPrefix = LlmProxyCliChatConfig.CliChatProxyPath + "/";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map(LlmProxyCliChatConfig.CliChatProxyPath + "/{**rest}", static async context =>
        {
            var settings = context.RequestServices
                .GetRequiredService<ILlmProxySettingsService>()
                .GetSettings();
            if (!settings.GrokLlmProxyEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var plan = settings.ResolvedPlan;
            var saverHasWork = !plan.IsNoOp && plan.GrokAllowlist.Count > 0;
            var captureSink = settings.TokenSaverCaptureEnabled
                ? context.RequestServices.GetService<ICompressionCaptureSink>()
                : null;
            // Grok 1.0.5 speaks two body shapes on this base (grok-4.6 → /responses,
            // grok-build / API mode → /chat/completions); the composite handles both with
            // Grok's own tool allowlist.
            var transform = settings.GrokTokenSaverEnabled && saverHasWork
                ? new CliChatBodyTransform(plan, captureSink)
                : (ILlmProxyBodyTransform?)null;
            var exchangeSink = context.RequestServices.GetRequiredService<ILlmProxyExchangeSink>();

            var upstreamHost = LlmProxyCliChatConfig.ResolveUpstreamHost(settings.GrokLlmProxyMode);
            var target = LlmProxyRelay.BuildTarget(context.Request, upstreamHost, PathPrefix);
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                context.RequestServices.GetRequiredService<ILlmProxyEventSink>(),
                target,
                "cli-chat",
                "Grok proxy",
                bodyTransform: transform,
                context.RequestAborted,
                exchangeSink);
        }).WithName("LlmCliChatProxy");
    }
}
