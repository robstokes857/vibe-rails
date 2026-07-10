using VibeRails.Auth;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Middleware;

public class CookieAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthService _authService;

    public CookieAuthMiddleware(RequestDelegate next, IAuthService authService)
    {
        _next = next;
        _authService = authService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var isWebSocketRequest = IsWebSocketHandshake(context);

        // Skip auth for bootstrap, health check, local LLM proxy, and CORS preflight requests.
        // The proxy route performs its own loopback/token auth and must also receive Codex's
        // OAuth discovery fallbacks under /.well-known/.../llm/openai/... instead of returning
        // the dashboard auth HTML to the Codex TUI.
        if (path.StartsWith("/auth/bootstrap") ||
            path.Equals("/api/v1/context", StringComparison.OrdinalIgnoreCase) ||
            IsOpenAiProxyPath(path) ||
            context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Validate cookie or header (header used by VSCode webview which can't set cookies)
        var token = context.Request.Cookies["viberails_session"]
            ?? context.Request.Headers["viberails_session"].FirstOrDefault();

        // Internal server-to-server ClientWebSocket connections (e.g. parent→child tab proxy)
        // cannot use cookies and send the session token as a WebSocket subprotocol instead.
        // Browser WebSocket connections always send the HttpOnly cookie automatically.
        // VSCode webview WebSocket connections pass the token as a query parameter since
        // the webview sandbox does not share cookies with the extension host.
        if (string.IsNullOrEmpty(token) && isWebSocketRequest)
        {
            token = context.WebSockets.WebSocketRequestedProtocols
                .FirstOrDefault(x => _authService.ValidateToken(x))
                ?? context.Request.Query["viberails_session"].FirstOrDefault();
        }

        if (!_authService.ValidateToken(token))
        {
            // For WebSocket upgrades, reject with 403 (can't redirect)
            if (isWebSocketRequest)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Unauthorized. Visit /auth/bootstrap to authenticate.");
                return;
            }

            // API calls should not be redirected (fetch/XHR expects JSON/status codes).
            if (path.StartsWith("/api/"))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            // Browser page/static requests - show error page (can't auto-redirect without code)
            context.Response.StatusCode = 403;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(STRINGS.AUTH_REQUIRED_HTML);
            return;
        }

        // Tab token required on API routes and WebSocket connections only.
        // Static files and page loads are exempt — the token lives in sessionStorage
        // which is populated after bootstrap, before any API calls are made.
        if (isWebSocketRequest || path.Contains("/api/", StringComparison.OrdinalIgnoreCase))
        {
            if (isWebSocketRequest)
            {
                var tabToken = context.WebSockets.WebSocketRequestedProtocols
                    .Select(x => NormalizeTabToken(x))
                    .FirstOrDefault(x => _authService.ValidateTabToken(x));
                if (tabToken == default)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("Unauthorized. Invalid tab token.");
                    return;
                }
                // Stash the accepted subprotocol so route handlers can echo it back in AcceptWebSocketAsync.
                // The browser's WS handshake requires the server to acknowledge the requested subprotocol.
                context.Items["viberails_accepted_subprotocol"] = tabToken;
            }
            else
            {
                var tabToken = context.Request.Headers["viberails_tab"].FirstOrDefault();
                if (!_authService.ValidateTabToken(tabToken))
                {
                    if (path.StartsWith("/api/"))
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized");
                        return;
                    }

                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync(STRINGS.AUTH_REQUIRED_HTML);
                    return;
                }
            }
        }


        // Authenticated - continue to next middleware
        await _next(context);
    }

    private static string NormalizeTabToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private static bool IsPathOrChild(string path, string prefix)
    {
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenAiProxyPath(string path)
    {
        return IsPathOrChild(path, LlmProxyCodexConfig.OpenAiProxyPath)
            || IsWellKnownOpenAiProxyPath(path);
    }

    private static bool IsWellKnownOpenAiProxyPath(string path)
    {
        return path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase)
            && path.Contains(LlmProxyCodexConfig.OpenAiProxyPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebSocketHandshake(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            return true;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var upgrade = context.Request.Headers.Upgrade.ToString();
        if (!upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var connection = context.Request.Headers.Connection.ToString();
        return connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }
}
