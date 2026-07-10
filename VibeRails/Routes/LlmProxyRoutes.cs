using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using VibeRails.Auth;
using VibeRails.Interfaces;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Routes;

public static class LlmProxyRoutes
{
    private const string UpstreamScheme = "https";
    private const string ApiUpstreamHost = "api.openai.com";
    private const string SubscriptionUpstreamHost = "chatgpt.com";
    private const string PathPrefix = LlmProxyCodexConfig.OpenAiProxyPath + "/";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    // Local-only headers stripped before forwarding upstream in BOTH modes: per-hop specifics plus
    // the proxy's own auth handshake (cookie + session/tab tokens), so the upstream provider never
    // receives VibeRails' local auth secrets. Mirrors the Anthropic proxy.
    private static readonly HashSet<string> LocalOnlyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Cookie",
        LlmProxyCodexConfig.SessionHeaderName,
        LlmProxyCodexConfig.TabHeaderName
    };

    public static void Map(WebApplication app)
    {
        app.Map(LlmProxyCodexConfig.OpenAiProxyPath + "/{**rest}", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            IAuthService authService,
            IAppEventBus appEventBus,
            ILlmProxySettingsService proxySettings,
            CancellationToken cancellationToken) =>
        {
            await ProxyAsync(
                context,
                httpClientFactory,
                authService,
                appEventBus,
                proxySettings,
                BuildOpenAiUri,
                cancellationToken);
        }).WithName("LlmOpenAiProxy");

    }

    private static async Task ProxyAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IAuthService authService,
        IAppEventBus appEventBus,
        ILlmProxySettingsService proxySettings,
        Func<HttpRequest, string, Uri> buildTarget,
        CancellationToken cancellationToken)
    {
        var settings = proxySettings.GetSettings();
        if (!settings.CodexLlmProxyEnabled)
        {
            // Feature is off: behave as if the proxy endpoint doesn't exist rather than leaving an
            // always-on relay to the upstream provider.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var mode = settings.CodexLlmProxyMode;
        var target = buildTarget(context.Request, mode);
        if (!IsProxyAuthenticated(context, authService))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        using var upstreamRequest = CreateUpstreamRequest(context, target);
        var http = httpClientFactory.CreateClient();
        using var upstreamResponse = await http.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Only a real, authenticated relay reaching OpenAI/ChatGPT lights the proxy indicator.
        // Keep the event display-only: no query string, request content, or local auth headers.
        appEventBus.PublishProxyActivity(
            source: "Codex proxy",
            label: context.Request.Method,
            target: target.GetLeftPart(UriPartial.Path),
            status: ((int)upstreamResponse.StatusCode).ToString());

        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyHeaders(upstreamResponse.Headers, context.Response.Headers);
        CopyHeaders(upstreamResponse.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");

        // Codex uses wire_api="responses" (SSE); disable response buffering so the TUI renders
        // tokens live rather than in buffered bursts (mirrors the Anthropic proxy).
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static Uri BuildUri(HttpRequest request, string host, string path)
    {
        var builder = new UriBuilder
        {
            Scheme = UpstreamScheme,
            Host = host,
            Path = path
        };

        var query = request.QueryString.Value;
        if (!string.IsNullOrEmpty(query))
            builder.Query = query[0] == '?' ? query[1..] : query;

        return builder.Uri;
    }

    private static bool IsProxyAuthenticated(HttpContext context, IAuthService authService)
    {
        var sessionToken = context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyCodexConfig.TabHeaderName].FirstOrDefault();
        return authService.ValidateToken(sessionToken) && authService.ValidateTabToken(tabToken);
    }

    private static HttpRequestMessage CreateUpstreamRequest(HttpContext context, Uri target)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        if (RequestCanHaveBody(context.Request))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        return request;
    }

    internal static Uri BuildOpenAiUri(HttpRequest request, string mode)
    {
        var path = request.Path.ToUriComponent();
        var rest = path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path.AsSpan(PathPrefix.Length)
            : ReadOnlySpan<char>.Empty;
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);

        var targetHost = normalizedMode == CodexLlmProxySettings.ModeApi
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;
        return BuildUri(request, targetHost, BuildUpstreamPath(rest));
    }

    private static string BuildUpstreamPath(ReadOnlySpan<char> rest) =>
        rest.IsEmpty ? "/" : string.Concat("/", rest);

    private static bool RequestCanHaveBody(HttpRequest request)
    {
        if (request.ContentLength.GetValueOrDefault() > 0)
            return true;

        return request.Headers.ContainsKey("Transfer-Encoding");
    }

    private static bool ShouldSkipRequestHeader(string headerName) =>
        HopByHopHeaders.Contains(headerName) || LocalOnlyHeaders.Contains(headerName);

    private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            destination[header.Key] = header.Value.ToArray();
        }
    }
}
