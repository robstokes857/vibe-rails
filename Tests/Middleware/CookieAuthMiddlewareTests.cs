using Microsoft.AspNetCore.Http;
using VibeRails.Auth;
using VibeRails.Middleware;
using Xunit;

namespace Tests.Middleware;

/// <summary>
/// Pins the auth gate for the paths a leaked token could turn into host access. The high-value
/// invariant: <c>/mcp</c> (whose tools can open a host shell) requires BOTH the session and tab
/// tokens, exactly like <c>/api/</c> — a session token alone is never enough. Also pins that the
/// only unauthenticated surfaces are the bootstrap flow and the bare <c>/health</c> probe, and
/// that query-string session tokens are no longer accepted for WebSocket upgrades (they leak into
/// request logs).
/// </summary>
public sealed class CookieAuthMiddlewareTests
{
    private static readonly AuthService Auth = new();

    private static CookieAuthMiddleware Build(out bool[] reachedNext)
    {
        var flag = new bool[1];
        reachedNext = flag;
        return new CookieAuthMiddleware(
            _ =>
            {
                flag[0] = true;
                return Task.CompletedTask;
            },
            Auth);
    }

    private static DefaultHttpContext Request(
        string path, string? session = null, string? tab = null, string? query = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (query is not null)
            ctx.Request.QueryString = new QueryString(query);
        if (session is not null)
            ctx.Request.Headers["viberails_session"] = session;
        if (tab is not null)
            ctx.Request.Headers["viberails_tab"] = tab;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/auth/bootstrap")]
    public async Task UnauthenticatedProbeAndBootstrap_PassThrough(string path)
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request(path);

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }

    [Fact]
    public async Task Context_NowRequiresAuth()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request("/api/v1/context"); // no tokens

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithSessionTokenOnly_IsRejected()
    {
        // The core of the finding: a session token alone must NOT reach /mcp, whose tools can
        // open a host shell.
        var middleware = Build(out var reachedNext);
        var ctx = Request("/mcp", session: Auth.GetInstanceToken());

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithBothTokens_IsAllowed()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request("/mcp", session: Auth.GetInstanceToken(), tab: Auth.GetTabToken());

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }

    [Fact]
    public async Task McpChildPath_WithSessionTokenOnly_IsRejected()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request("/mcp/message", session: Auth.GetInstanceToken());

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Api_WithBothTokens_IsAllowed()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request(
            "/api/v1/context", session: Auth.GetInstanceToken(), tab: Auth.GetTabToken());

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }
}
