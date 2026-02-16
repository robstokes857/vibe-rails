using VibeRails.Auth;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class AuthRoutes
{
    public static void Map(WebApplication app)
    {
        // Bootstrap endpoint - validates one-time code and sets auth cookie
        app.MapGet("/auth/bootstrap", (HttpContext context, IAuthService authService, string? code) =>
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

            // Show the redirect page
            return Results.Content(STRINGS.AUTH_BOOTSTRAP_HTML, "text/html");
        }).WithName("AuthBootstrap");
    }
}
