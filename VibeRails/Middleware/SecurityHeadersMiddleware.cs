namespace VibeRails.Middleware;

/// <summary>
/// Applies browser hardening headers to every loopback response. VibeRails pages and APIs are
/// authenticated and can contain terminal transcripts, repository paths, and tool output, so they
/// must not be stored in a shared browser cache or embedded by another origin.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'self'; " +
        "frame-src 'self'; " +
        "form-action 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com data:; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self' http: https: ws: wss:; " +
        "worker-src 'self' blob:; " +
        "manifest-src 'self'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        // SAMEORIGIN is intentional: the MCP Explorer embeds the same-origin mcp-remote.html
        // document in a sandboxed iframe. CSP frame-ancestors enforces the same boundary.
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        headers["Pragma"] = "no-cache";

        return _next(context);
    }
}
