using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Local reverse proxy for Codex: catches <c>/llm/openai/{**rest}</c> and forwards to the OpenAI API
/// (<c>api.openai.com</c>) or the ChatGPT backend (<c>chatgpt.com</c>) depending on the configured
/// auth mode. Codex points here via its <c>model_providers</c> config. The shared streaming relay,
/// header filtering, auth gate, and disconnect handling live in <see cref="LlmProxyRelay"/>.
///
/// Handlers are plain <see cref="RequestDelegate"/>s (dependencies resolved from
/// <c>RequestServices</c>) rather than parameter-injected minimal-API lambdas: the Request Delegate
/// Generator only runs in the host Web-SDK project, so injected lambdas compiled in this library
/// would fall back to the reflection-based factory and break AOT.
/// </summary>
public static class LlmProxyRoutes
{
    private const string ApiUpstreamHost = "api.openai.com";
    private const string SubscriptionUpstreamHost = "chatgpt.com";
    private const string PathPrefix = LlmProxyCodexConfig.OpenAiProxyPath + "/";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.Map(LlmProxyCodexConfig.OpenAiProxyPath + "/{**rest}", static async context =>
        {
            var settings = context.RequestServices
                .GetRequiredService<ILlmProxySettingsService>()
                .GetSettings();
            if (!settings.CodexLlmProxyEnabled)
            {
                // Feature is off: behave as if the endpoint doesn't exist rather than leaving an
                // always-on relay to the upstream provider.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var plan = settings.ResolvedPlan;
            var saverHasWork = !plan.IsNoOp && plan.CodexAllowlist.Count > 0;
            var captureSink = settings.TokenSaverCaptureEnabled
                ? context.RequestServices.GetService<ICompressionCaptureSink>()
                : null;
            var transform = settings.CodexTokenSaverEnabled && saverHasWork
                ? new CodexBodyTransform(plan, captureSink)
                : null;
            // Every authenticated request relayed through a proxy is an exchange. This is
            // deliberately not configurable: if the proxy handles it, the exchange log records it.
            var exchangeSink = context.RequestServices.GetRequiredService<ILlmProxyExchangeSink>();

            var target = BuildOpenAiUri(context.Request, settings.CodexLlmProxyMode);
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                context.RequestServices.GetRequiredService<ILlmProxyEventSink>(),
                target,
                "openai",
                "Codex proxy",
                bodyTransform: transform,
                context.RequestAborted,
                exchangeSink);
        }).WithName("LlmOpenAiProxy");
    }

    /// <summary>
    /// Resolves the upstream target for the given auth mode: API mode goes to <c>api.openai.com</c>,
    /// subscription mode to the ChatGPT backend. Only the host differs — the path/query are relayed.
    /// </summary>
    internal static Uri BuildOpenAiUri(HttpRequest request, string mode)
    {
        var targetHost = CodexLlmProxySettings.NormalizeMode(mode) == CodexLlmProxySettings.ModeApi
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;

        return LlmProxyRelay.BuildTarget(request, targetHost, PathPrefix);
    }
}
