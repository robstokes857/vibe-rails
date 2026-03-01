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

        // Skip auth for bootstrap, health check, and CORS preflight requests
        if (path.StartsWith("/auth/bootstrap") ||
            path.Equals("/api/v1/context", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Validate cookie or header (header used by VSCode webview which can't set cookies)
        var token = context.Request.Cookies["viberails_session"]
            ?? context.Request.Headers["viberails_session"].FirstOrDefault();

        // WebSocket requests from browser JS cannot set custom headers.
        // Allow token in query string for terminal WS handshake.
        if (string.IsNullOrEmpty(token) && isWebSocketRequest)
        {
            token = context.Request.Query["viberails_session"].FirstOrDefault();
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

        // Authenticated - continue to next middleware
        await _next(context);
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
