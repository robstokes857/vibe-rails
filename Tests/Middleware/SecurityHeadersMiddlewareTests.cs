using Microsoft.AspNetCore.Http;
using VibeRails.Middleware;
using Xunit;

namespace Tests.Middleware;

public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task AddsBrowserHardeningAndNoStoreHeaders()
    {
        var reachedNext = false;
        var middleware = new SecurityHeadersMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, context.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("SAMEORIGIN", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("no-store", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("no-cache", context.Response.Headers["Pragma"].ToString());
    }

    [Fact]
    public async Task PolicyDisablesObjectsAndCrossOriginFraming()
    {
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        var policy = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src *", policy, StringComparison.Ordinal);
    }
}
