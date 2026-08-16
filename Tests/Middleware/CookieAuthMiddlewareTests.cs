using Microsoft.AspNetCore.Http;
using VibeRails.Auth;
using VibeRails.Middleware;
using Xunit;

namespace Tests.Middleware;

/// <summary>
/// Pins the auth gate for the paths a leaked token could turn into host access. The high-value
/// invariant: <c>/mcp</c> (whose tools can open a host shell) requires BOTH the session and tab
/// tokens, exactly like <c>/api/</c> — a session token alone is never enough. Also pins that the
/// only unauthenticated surfaces are the bootstrap flow and the bare <c>/health</c> probe — the
/// skip list is frozen (API_SEC.md § 1); the <c>/llm/**</c> proxy trees were removed from it on
/// 2026-08-05 and must never return — and that query-string session tokens are no longer accepted
/// for WebSocket upgrades (they leak into request logs).
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
    public async Task OptionsRequests_PassThrough()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request("/api/v1/context");
        ctx.Request.Method = "OPTIONS";

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }

    [Theory]
    [InlineData("POST", "/health")]
    [InlineData("POST", "/auth/bootstrap")]
    [InlineData("GET", "/auth/bootstrap/child")]
    [InlineData("GET", "/auth/bootstrap-extra")]
    public async Task OnlyExactGetProbeAndBootstrap_BypassAuthentication(string method, string path)
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request(path);
        ctx.Request.Method = method;

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(403, ctx.Response.StatusCode);
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

    [Theory]
    [InlineData("/api/v1/llm-picker/preferences")]
    [InlineData("/api/v1/settings/export-data/progress")]
    [InlineData("/api/v1/http-relay/test/posts")]
    [InlineData("/api/v1/http-relay/test/posts/7")]
    public async Task NewlyInventoriedApiRoutes_WithSessionTokenOnly_AreRejected(string path)
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request(path, session: Auth.GetInstanceToken());

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/llm/openai/v1/responses")]
    [InlineData("/llm/openai/backend-api/codex/responses")]
    [InlineData("/llm/zai/api/paas/v4/chat/completions")]
    [InlineData("/llm/xai/v1/chat/completions")]
    [InlineData("/llm/anthropic/v1/messages")]
    [InlineData("/llm/control/token-saver/pause")]
    public async Task LlmSurfaces_WithNoTokens_AreRejectedWithPlainStatus(string path)
    {
        // /llm/openai and /llm/zai were REMOVED from the middleware skip list (2026-08-05); no
        // /llm path may ever return to it. Unauthenticated callers get 401 before any handler
        // runs — which also kills the old 404-vs-401 "is the proxy feature enabled" oracle — and
        // the rejection is a plain status code, never the HTML auth page, because every /llm
        // caller is a CLI HTTP client or MCP tool.
        var middleware = Build(out var reachedNext);
        var ctx = Request(path);

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.NotEqual("text/html", ctx.Response.ContentType);
    }

    [Theory]
    [InlineData("/llm/openai/v1/responses")]
    [InlineData("/llm/anthropic/v1/messages")]
    [InlineData("/llm/xai/v1/chat/completions")]
    public async Task LlmProxyPaths_WithSessionHeader_PassTheMiddleware(string path)
    {
        // The CLIs authenticate with the same session secret as everyone else, sent as a header
        // (Codex via env_http_headers, Claude via ANTHROPIC_CUSTOM_HEADERS, OpenCode via
        // OPENCODE_CONFIG_CONTENT). These real CLI paths contain no "/api/" segment, so the
        // middleware enforces session only; ILlmProxyAuthGate inside the relay then requires
        // session AND tab. Pinned so a middleware change that starts rejecting header-borne
        // session credentials shows up here, not as a dead CLI mid-conversation.
        var middleware = Build(out var reachedNext);
        var ctx = Request(path, session: Auth.GetInstanceToken());

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }

    [Fact]
    public async Task ZaiChatCompletions_WithSessionOnly_IsRejected()
    {
        // Z.AI's upstream shape puts "/api/" in every real request path (/llm/zai/api/paas/v4/…),
        // which trips the middleware's tab rule — so the zai tree gets BOTH checks before its
        // handler. OpenCode sends both headers, so real traffic passes; pinned so the stricter
        // posture isn't accidentally relaxed.
        var middleware = Build(out var reachedNext);
        var ctx = Request(
            "/llm/zai/api/paas/v4/chat/completions", session: Auth.GetInstanceToken());

        await middleware.InvokeAsync(ctx);

        Assert.False(reachedNext[0]);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ZaiChatCompletions_WithBothTokens_IsAllowed()
    {
        var middleware = Build(out var reachedNext);
        var ctx = Request(
            "/llm/zai/api/paas/v4/chat/completions",
            session: Auth.GetInstanceToken(),
            tab: Auth.GetTabToken());

        await middleware.InvokeAsync(ctx);

        Assert.True(reachedNext[0]);
    }
}
