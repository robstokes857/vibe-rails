using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using VibeRails.Auth;
using VibeRails.Interfaces;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Routes;

/// <summary>
/// Local reverse proxy for Claude Code: catches <c>/llm/anthropic/{**rest}</c> and forwards to
/// <c>https://api.anthropic.com/{rest}</c>. Claude points here via <c>ANTHROPIC_BASE_URL</c>.
///
/// v1 is a pure passthrough — the request body is streamed upstream untouched and the SSE response
/// is streamed back verbatim. The token-trimming transforms from <c>token_saving_plan.md</c> §3
/// (rewriting shell <c>tool_result</c> strings) will hook the request body at the marked seam in
/// <see cref="CreateUpstreamRequest"/>; nothing is rewritten yet.
///
/// Kept separate from <see cref="LlmProxyRoutes"/> (OpenAI/Codex) on purpose: the two providers
/// will diverge (Anthropic buffers + rewrites request bodies, OpenAI streams through) and this
/// route masks the session/tab tokens in its debug logging.
/// </summary>
public static class LlmAnthropicProxyRoutes
{
    private const string UpstreamScheme = "https";
    private const string UpstreamHost = LlmProxyClaudeConfig.UpstreamHost;
    private const string PathPrefix = LlmProxyClaudeConfig.AnthropicProxyPath + "/";

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

    // Local-only headers: the proxy's own auth handshake plus per-hop specifics. Stripped before
    // forwarding upstream so Anthropic never sees our session/tab tokens.
    private static readonly HashSet<string> LocalOnlyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Cookie",
        "Content-Length",
        LlmProxyClaudeConfig.SessionHeaderName,
        LlmProxyClaudeConfig.TabHeaderName
    };

    public static void Map(WebApplication app)
    {
        app.Map(LlmProxyClaudeConfig.AnthropicProxyPath + "/{**rest}", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            IAuthService authService,
            IAppEventBus appEventBus,
            ILlmProxySettingsService proxySettings,
            CancellationToken cancellationToken) =>
        {
            if (!proxySettings.GetSettings().ClaudeLlmProxyEnabled)
            {
                // Feature is off: behave as if the proxy endpoint doesn't exist rather than leaving
                // an always-on relay to api.anthropic.com.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var target = BuildAnthropicUri(context.Request);
            if (!IsProxyHeaderAuthenticated(context, authService))
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

            // Feed the shared activity light: a cheap, non-blocking ping (the WS handler just
            // TryWrites to a channel) that never carries the tool/session tokens — only where the
            // request went and its status. "Claude proxy" is the stable source the UI groups by.
            appEventBus.PublishProxyActivity(
                source: "Claude proxy",
                label: context.Request.Method,
                target: target.GetLeftPart(UriPartial.Path),
                status: ((int)upstreamResponse.StatusCode).ToString());

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyHeaders(upstreamResponse.Headers, context.Response.Headers);
            CopyHeaders(upstreamResponse.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");

            // The model response is SSE — stream it straight back, chunk-by-chunk, untouched. The
            // TUI needs it live, so disable response buffering before we start copying.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }).WithName("LlmAnthropicProxy");
    }

    private static bool IsProxyHeaderAuthenticated(HttpContext context, IAuthService authService)
    {
        var sessionToken = context.Request.Headers[LlmProxyClaudeConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyClaudeConfig.TabHeaderName].FirstOrDefault();
        return authService.ValidateToken(sessionToken) && authService.ValidateTabToken(tabToken);
    }

    private static HttpRequestMessage CreateUpstreamRequest(HttpContext context, Uri target)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        if (RequestCanHaveBody(context.Request))
        {
            // v1: stream the request body upstream untouched (pure passthrough).
            // SEAM: token_saving_plan.md §3 transforms (shell tool_result minification) plug in
            // here — buffer the body, rewrite qualifying tool_result strings, then forward. Must
            // fail open: any parse/transform error falls back to the original bytes.
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

    private static Uri BuildAnthropicUri(HttpRequest request)
    {
        var path = request.Path.ToUriComponent();
        var rest = path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path.AsSpan(PathPrefix.Length)
            : ReadOnlySpan<char>.Empty;

        var builder = new UriBuilder
        {
            Scheme = UpstreamScheme,
            Host = UpstreamHost,
            Path = rest.IsEmpty ? "/" : string.Concat("/", rest)
        };

        var query = request.QueryString.Value;
        if (!string.IsNullOrEmpty(query))
            builder.Query = query[0] == '?' ? query[1..] : query;

        return builder.Uri;
    }

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
