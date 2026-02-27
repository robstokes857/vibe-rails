using VibeRails.Auth;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class AuthRoutes
{
    public static void Map(WebApplication app)
    {
        // Bootstrap endpoint - validates one-time code and sets auth cookie
        app.MapGet("/auth/bootstrap", (HttpContext context, IAuthService authService, string? code, string? redirect) =>
        {
            // Validate the bootstrap code
            if (!authService.ValidateAndConsumeBootstrapCode(code))
            {
                // Invalid, expired, or already used code
                context.Response.StatusCode = 403;
                return Results.Content(STRINGS.AUTH_INVALID_CODE_HTML, "text/html");
            }

            // Code is valid - set the session cookie
            var token = authService.GetInstanceToken();

            context.Response.Cookies.Append("viberails_session", token, new CookieOptions
            {
                HttpOnly = true,              // Prevent JavaScript access (XSS protection)
                SameSite = SameSiteMode.Lax,  // Allow cookie on redirects
                Secure = false,               // localhost uses HTTP not HTTPS
                Path = "/",                   // Cookie applies to all routes
                IsEssential = true            // Exempt from GDPR consent requirements
            });

            // Redirect to the requested page, or default to /
            var destination = (!string.IsNullOrWhiteSpace(redirect) && redirect.StartsWith('/'))
                ? redirect
                : "/";

            var html = STRINGS.AUTH_BOOTSTRAP_HTML.Replace("window.location.replace('/')", $"window.location.replace('{destination}')");
            return Results.Content(html, "text/html");
        }).WithName("AuthBootstrap");
    }
}
