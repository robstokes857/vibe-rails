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
            if (!context.RequestServices.GetRequiredService<ILlmProxyAuthGate>().IsRequestAuthenticated(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
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
            var transform = settings.CodexTokenSaverEnabled && saverHasWork
                ? new CodexBodyTransform(plan)
                : null;
            // Every authenticated request relayed through a proxy is an exchange. This is
            // deliberately not configurable: if the proxy handles it, the exchange log records it.
            var exchangeSink = context.RequestServices.GetRequiredService<ILlmProxyExchangeSink>();

            var events = context.RequestServices.GetRequiredService<ILlmProxyEventSink>();
            var target = await LlmProxyRelay.ApplyEnterpriseGatewayAsync(
                context, events, "Codex proxy", BuildOpenAiUri(context.Request, settings.CodexLlmProxyMode),
                settings.CodexEnterpriseNetworkProxy, OpenAiApiPath(context.Request, settings.CodexLlmProxyMode));
            if (target is null)
                return;
            await LlmProxyRelay.HandleAsync(
                context,
                context.RequestServices.GetRequiredService<IHttpClientFactory>(),
                context.RequestServices.GetRequiredService<ILlmProxyAuthGate>(),
                events,
                target,
                "openai",
                "Codex proxy",
                bodyTransform: transform,
                context.RequestAborted,
                exchangeSink,
                // Already bound to the saved gateway (blank otherwise) by the settings snapshot.
                settings.CodexGatewayBearerToken,
                settings.CodexGatewayAuthMode,
                settings.CodexGatewayApiKeyHeader);
        }).WithName("LlmOpenAiProxy");
    }

    /// <summary>
    /// Resolves the upstream target: API requests go to <c>api.openai.com</c>, subscription requests
    /// to the ChatGPT backend. Only the host differs — the path/query are relayed. The request's own
    /// API path decides, as in <see cref="OpenAiApiPath(HttpRequest, string)"/>: a running terminal
    /// keeps the path chosen at launch after Settings changes the auth mode, and its requests must
    /// keep reaching the host that path belongs to. The mode decides only for any other path.
    /// </summary>
    internal static Uri BuildOpenAiUri(HttpRequest request, string mode)
    {
        var targetHost = OpenAiApiPath(request, mode) == OpenAiApiPath(CodexLlmProxySettings.ModeApi)
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;

        return LlmProxyRelay.BuildTarget(request, targetHost, PathPrefix);
    }

    /// <summary>
    /// The provider API base an enterprise gateway replaces for the given auth mode; the endpoint
    /// suffix after it (for example <c>/responses</c>) and the query are kept.
    /// </summary>
    internal static string OpenAiApiPath(string mode) =>
        CodexLlmProxySettings.NormalizeMode(mode) == CodexLlmProxySettings.ModeApi
            ? "/v1"
            : "/backend-api/codex";

    // A running terminal keeps the path chosen at launch even after Settings changes its
    // auth mode. Strip that actual path when sending to a gateway, not the new launch mode.
    private static string OpenAiApiPath(HttpRequest request, string mode) =>
        request.Path.StartsWithSegments(LlmProxyCodexConfig.OpenAiProxyPath + "/backend-api/codex")
            ? "/backend-api/codex"
            : request.Path.StartsWithSegments(LlmProxyCodexConfig.OpenAiProxyPath + "/v1")
                ? "/v1" : OpenAiApiPath(mode);
}
