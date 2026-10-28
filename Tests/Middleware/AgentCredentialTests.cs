using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Auth;
using VibeRails.Middleware;
using VibeRails.Services.LlmProxy;
using Xunit;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;

namespace Tests.Middleware;

public sealed class AgentCredentialTests
{
    [Theory]
    [InlineData("GET", "/api/v1/llm-picker/preferences", true)]
    [InlineData("POST", "/api/v1/board/cards/VB-ABCDE-123/launch", true)]
    [InlineData("POST", "/api/v1/sessions/62062844-5d76-4338-a806-fca543721887/sharing-links", true)]
    // request_review (VB-50 slice E): create and read the disclosure only; never list, preview, send, decide, cancel or delete.
    [InlineData("POST", "/api/v1/review-requests", true)]
    [InlineData("GET", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887/disclosure", true)]
    [InlineData("GET", "/api/v1/review-requests", false)]
    [InlineData("GET", "/api/v1/review-requests/staged", false)]
    [InlineData("GET", "/api/v1/review-requests/commit-approvals", false)]
    [InlineData("GET", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887", false)]
    [InlineData("GET", "/api/v1/review-requests/not-a-guid/disclosure", false)]
    [InlineData("POST", "/api/v1/review-requests/", false)]
    [InlineData("POST", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887/send", false)]
    [InlineData("POST", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887/decisions", false)]
    [InlineData("POST", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887/cancel", false)]
    [InlineData("DELETE", "/api/v1/review-requests/62062844-5d76-4338-a806-fca543721887", false)]
    [InlineData("POST", "/api/v1/review-inbox/rvw_0123456789abcdef0123456789abcdef/approve", false)]
    [InlineData("POST", "/api/v1/settings", false)]
    [InlineData("GET", "/api/v1/board/boards/board-a/every-context", false)]
    [InlineData("PUT", "/api/v1/board/boards/board-a/every-context", false)]
    [InlineData("GET", "/api/v1/board/app-context", false)]
    [InlineData("PUT", "/api/v1/board/app-context", false)]
    [InlineData("PUT", "/api/v1/board/cards/card-a/pin", false)]
    [InlineData("GET", "/api/v1/settings/security-challenge", false)]
    [InlineData("POST", "/api/v1/settings/pin", false)]
    [InlineData("POST", "/api/v1/terminal/start", false)]
    [InlineData("POST", "/api/v1/agent-tools/terminal/open", false)]
    [InlineData("POST", "/api/v1/shutdown", false)]
    [InlineData("POST", "/mcp", false)]
    [InlineData("OPTIONS", "/api/v1/settings", false)]
    [InlineData("POST", "/llm/openai/v1/responses", false)]
    public async Task ToolCredentialsReachOnlyTheReviewedRoutes(string method, string path, bool allowed)
    {
        var credentials = new AgentCredentialService();
        var contact = credentials.ForTools("tab-a");
        credentials.BindSession("tab-a", "session-a");
        var host = new Mock<ITerminalTabHostService>();
        host.Setup(h => h.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new TerminalTabStatusResponse("tab-a", DateTime.UtcNow, true, "session-a")]);
        var reached = false;
        using var services = new ServiceCollection().AddSingleton(credentials).AddSingleton(host.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers["viberails_session"] = contact.Session;
        context.Request.Headers["viberails_tab"] = contact.Tab;
        await new CookieAuthMiddleware(_ => { reached = true; return Task.CompletedTask; }, new AuthService()).InvokeAsync(context);
        Assert.Equal(allowed, reached);
        if (!allowed) Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public void CredentialsArePairedRevocableAndSeparatedFromDashboardAndOtherLaunches()
    {
        var auth = new AuthService();
        var credentials = new AgentCredentialService();
        var tool = credentials.ForTools("tab");
        credentials.BeginSession("first");
        var first = credentials.ProxyContact;
        var proxyGate = new LlmProxyAuthGateAdapter(auth, credentials);
        Assert.False(auth.ValidateToken(tool.Session));
        Assert.False(auth.ValidateToken(first.Session));
        Assert.True(proxyGate.ValidateTokens(first.Session, first.Tab));
        Assert.False(proxyGate.ValidateTokens(tool.Session, tool.Tab));
        Assert.False(proxyGate.ValidateTokens(first.Session, tool.Tab));
        credentials.BeginSession("second");
        Assert.False(proxyGate.ValidateTokens(first.Session, first.Tab));
        credentials.Revoke("tab");
        Assert.Null(credentials.Authenticate(tool.Session, tool.Tab));
    }

    [Theory]
    [InlineData(false, "session-a", true)]
    [InlineData(true, "session-b", true)]
    [InlineData(true, "session-a", false)]
    public async Task EndedReplacedOrUnconfirmedSessionsCannotUseToolContacts(bool active, string currentSession, bool confirmed)
    {
        var credentials = new AgentCredentialService();
        var contact = credentials.PrepareTools("tab-a");
        if (confirmed) credentials.BindSession("tab-a", "session-a");
        var host = new Mock<ITerminalTabHostService>();
        host.Setup(h => h.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new TerminalTabStatusResponse("tab-a", DateTime.UtcNow, active, currentSession)]);
        using var services = new ServiceCollection().AddSingleton(credentials).AddSingleton(host.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/llm-picker/preferences";
        context.Request.Headers["viberails_session"] = contact.Session;
        context.Request.Headers["viberails_tab"] = contact.Tab;
        var reached = false;
        await new CookieAuthMiddleware(_ => { reached = true; return Task.CompletedTask; }, new AuthService()).InvokeAsync(context);
        Assert.False(reached);
        Assert.Equal(401, context.Response.StatusCode);
    }
}
