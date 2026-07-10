using System.Net;
using System.Net.Http.Headers;
using VibeRails.Auth;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Routes;

public static class LlmProxyRoutes
{
    private const string UpstreamScheme = "https";
    private const string ApiUpstreamHost = "api.openai.com";
    private const string SubscriptionUpstreamHost = "chatgpt.com";
    private const string SubscriptionPathPrefix = "/backend-api";
    private const string SubscriptionCodexPathPrefix = "/backend-api/codex";
    private const string SubscriptionDirectApiPathPrefix = "/api";
    private const string SubscriptionWhamPathPrefix = "/backend-api/wham";
    private const string PathPrefix = LlmProxyCodexConfig.OpenAiProxyPath + "/";
    private const string WellKnownPathPrefix = "/.well-known/";

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

    private static readonly HashSet<string> LocalOnlyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length"
    };

    private static readonly HashSet<string> ApiOnlyLocalHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
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
            ILlmProxySettingsService proxySettings,
            CancellationToken cancellationToken) =>
        {
            await ProxyAsync(
                context,
                httpClientFactory,
                authService,
                proxySettings,
                BuildOpenAiUri,
                cancellationToken);
        }).WithName("LlmOpenAiProxy");

        app.Map(WellKnownPathPrefix + "{**rest}", async (
            HttpContext context,
            IHttpClientFactory httpClientFactory,
            IAuthService authService,
            ILlmProxySettingsService proxySettings,
            CancellationToken cancellationToken) =>
        {
            if (!IsWellKnownOpenAiProxyRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await ProxyAsync(
                context,
                httpClientFactory,
                authService,
                proxySettings,
                BuildWellKnownOpenAiUri,
                cancellationToken);
        }).WithName("LlmOpenAiWellKnownProxy");
    }

    private static async Task ProxyAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IAuthService authService,
        ILlmProxySettingsService proxySettings,
        Func<HttpRequest, string, Uri> buildTarget,
        CancellationToken cancellationToken)
    {
        var mode = proxySettings.CodexLlmProxyMode;
        var target = buildTarget(context.Request, mode);
        if (!IsProxyAuthenticated(context, authService, mode))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        using var upstreamRequest = CreateUpstreamRequest(context, target, mode);
        var http = httpClientFactory.CreateClient();
        using var upstreamResponse = await http.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyHeaders(upstreamResponse.Headers, context.Response.Headers);
        CopyHeaders(upstreamResponse.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");

        await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static bool IsWellKnownOpenAiProxyRequest(HttpRequest request)
    {
        var path = request.Path.ToUriComponent();
        return path.StartsWith(WellKnownPathPrefix, StringComparison.OrdinalIgnoreCase)
            && path.Contains(LlmProxyCodexConfig.OpenAiProxyPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri BuildWellKnownOpenAiUri(HttpRequest request, string mode)
    {
        var path = request.Path.ToUriComponent();
        var markerIndex = path.IndexOf(LlmProxyCodexConfig.OpenAiProxyPath, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return BuildUri(request, SubscriptionUpstreamHost, path);

        var beforeProxyPath = path[..markerIndex];
        var restStart = markerIndex + LlmProxyCodexConfig.OpenAiProxyPath.Length;
        var rest = restStart < path.Length && path[restStart] == '/'
            ? path.AsSpan(restStart + 1)
            : path.AsSpan(restStart);
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);
        var targetHost = normalizedMode == CodexLlmProxySettings.ModeApi
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;
        var upstreamResourcePath = normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildApiUpstreamPath(rest)
            : BuildSubscriptionUpstreamPath(rest);

        return BuildUri(request, targetHost, string.Concat(beforeProxyPath, upstreamResourcePath));
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

    private static bool IsProxyAuthenticated(HttpContext context, IAuthService authService, string mode)
    {
        var sessionToken = context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyCodexConfig.TabHeaderName].FirstOrDefault();
        if (authService.ValidateToken(sessionToken) && authService.ValidateTabToken(tabToken))
            return true;

        return CodexLlmProxySettings.NormalizeMode(mode) == CodexLlmProxySettings.ModeSubscription
            && IsLoopbackRequest(context);
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
    }

    private static HttpRequestMessage CreateUpstreamRequest(HttpContext context, Uri target, string mode)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);

        if (RequestCanHaveBody(context.Request))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key, normalizedMode))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        return request;
    }

    private static Uri BuildOpenAiUri(HttpRequest request, string mode)
    {
        var path = request.Path.ToUriComponent();
        var rest = path.StartsWith(PathPrefix, StringComparison.Ordinal)
            ? path.AsSpan(PathPrefix.Length)
            : ReadOnlySpan<char>.Empty;
        var normalizedMode = CodexLlmProxySettings.NormalizeMode(mode);

        var targetHost = normalizedMode == CodexLlmProxySettings.ModeApi
            ? ApiUpstreamHost
            : SubscriptionUpstreamHost;
        var targetPath = normalizedMode == CodexLlmProxySettings.ModeApi
            ? BuildApiUpstreamPath(rest)
            : BuildSubscriptionUpstreamPath(rest);

        return BuildUri(request, targetHost, targetPath);
    }

    private static string BuildApiUpstreamPath(ReadOnlySpan<char> rest) =>
        rest.IsEmpty ? "/" : string.Concat("/", rest);

    private static string BuildSubscriptionUpstreamPath(ReadOnlySpan<char> rest)
    {
        if (rest.StartsWith("api/codex/", StringComparison.Ordinal))
        {
            return BuildCodexApiUpstreamPath(rest["api/codex/".Length..]);
        }

        if (rest.Equals("api/codex", StringComparison.Ordinal))
            return SubscriptionCodexPathPrefix;

        if (rest.StartsWith("api/", StringComparison.Ordinal))
        {
            var apiRest = rest[4..];
            return apiRest.IsEmpty
                ? string.Concat(SubscriptionDirectApiPathPrefix, "/")
                : string.Concat(SubscriptionDirectApiPathPrefix, "/", apiRest);
        }

        if (rest.Equals("api", StringComparison.Ordinal))
            return string.Concat(SubscriptionDirectApiPathPrefix, "/");

        return rest.IsEmpty
            ? string.Concat(SubscriptionPathPrefix, "/")
            : string.Concat(SubscriptionPathPrefix, "/", rest);
    }

    private static string BuildCodexApiUpstreamPath(ReadOnlySpan<char> codexRest)
    {
        if (codexRest.IsEmpty)
            return SubscriptionCodexPathPrefix;

        if (codexRest.StartsWith("ps/mcp", StringComparison.Ordinal))
            return string.Concat(SubscriptionCodexPathPrefix, "/", codexRest);

        return string.Concat(SubscriptionWhamPathPrefix, "/", codexRest);
    }

    private static bool RequestCanHaveBody(HttpRequest request)
    {
        if (request.ContentLength.GetValueOrDefault() > 0)
            return true;

        return request.Headers.ContainsKey("Transfer-Encoding");
    }

    private static bool ShouldSkipRequestHeader(string headerName, string normalizedMode)
    {
        if (HopByHopHeaders.Contains(headerName) || LocalOnlyHeaders.Contains(headerName))
            return true;

        return normalizedMode == CodexLlmProxySettings.ModeApi
            && ApiOnlyLocalHeaders.Contains(headerName);
    }

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
