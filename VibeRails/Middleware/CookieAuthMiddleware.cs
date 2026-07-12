using TokenSaver;
using VibeRails.Auth;
using VibeRails.Services;

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

        // Skip auth for bootstrap, the bare readiness probe, local LLM proxy, and CORS preflight
        // requests. The proxy route validates the session and tab headers injected into the model
        // provider. Everything else — including /api/v1/context, which leaks paths and the git
        // remote URL — requires full auth.
        if (path.StartsWith("/auth/bootstrap") ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            LlmProxyCodexConfig.IsOpenAiProxyPath(path) ||
            context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Validate cookie or header (header used by VSCode webview which can't set cookies)
        var token = context.Request.Cookies["viberails_session"]
            ?? context.Request.Headers["viberails_session"].FirstOrDefault();

        // WebSocket connections that can't use cookies (internal server-to-server ClientWebSocket,
        // the VSCode webview whose sandbox doesn't share cookies) send the session token as a
        // WebSocket subprotocol — both tokens are URL-safe base64 for exactly this reason.
        // Query-string tokens are deliberately NOT accepted: query strings end up in request logs.
        if (string.IsNullOrEmpty(token) && isWebSocketRequest)
        {
            token = context.WebSockets.WebSocketRequestedProtocols
                .FirstOrDefault(x => _authService.ValidateToken(x));
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

            // API and MCP calls should not be redirected (fetch/XHR/MCP clients expect status codes).
            if (path.StartsWith("/api/") || IsMcpPath(path))
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

        // Tab token required on API routes, the MCP endpoint, and WebSocket connections.
        // Static files and page loads are exempt — the token lives in sessionStorage
        // which is populated after bootstrap, before any API calls are made.
        // /mcp matters: its tools can open a host shell and send input, so a leaked session
        // token alone must never be enough to reach them.
        if (isWebSocketRequest
            || path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            || IsMcpPath(path))
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
                    if (path.StartsWith("/api/") || IsMcpPath(path))
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

    private static bool IsMcpPath(string path) =>
        path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTabToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return token.Replace("+", "-").Replace("/", "_").Replace("=", "");
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
