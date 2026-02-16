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

        // Skip auth for bootstrap and health check endpoints
        // Health check is used by VSCode extension to verify backend is running
        if (path.StartsWith("/auth/bootstrap") || path.Equals("/api/v1/IsLocal", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Skip auth for VSCode webview requests (different origin, can't use cookies)
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && origin.StartsWith("vscode-webview://", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Validate cookie for all other requests.
        var token = context.Request.Cookies["viberails_session"];

        if (!_authService.ValidateToken(token))
        {
            // For WebSocket upgrades, reject with 403 (can't redirect)
            if (context.WebSockets.IsWebSocketRequest)
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

        // Cookie valid - continue to next middleware
        await _next(context);
    }
}
