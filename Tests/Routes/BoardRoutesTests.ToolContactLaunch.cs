using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// A CLI's scoped tool contact reaches the card launch route for start_board_agent (VIBE-310).
/// It may send what that tool sends and nothing else: reviewer routing carries launch options,
/// including the provider's permission bypass, and belongs to the dashboard credentials.
/// </summary>
public sealed partial class BoardRoutesTests
{
    private const string ReviewerOverrideWithBypass =
        """{"intent":"code_review","review":{"override":{"selection":"base:claude","options":{"yolo":true}}}}""";

    [Fact]
    public async Task ToolContactLaunch_RefusesReviewerRoutingQuestionsAndOtherIntents()
    {
        using var created = await PostJsonAsync("/api/v1/board/cards", new { title = "Contact launch", assignee = "base:codex" });
        created.EnsureSuccessStatusCode();
        using var card = await ReadJsonAsync(created);
        var id = card.RootElement.GetProperty("id").GetString()!;
        var contact = BoundToolContact("contact-tab");

        foreach (var body in new[]
        {
            ReviewerOverrideWithBypass,
            """{"intent":"code_review","review":{"routing":{"mode":"fixed","mappings":[],"fallback":{"selection":"base:claude","options":{"yolo":true}}}}}""",
            """{"intent":"code_review","review":{}}""",
            """{"intent":"chat","question":"Why this design?"}""",
            """{"intent":"work","question":"x"}""",
            """{"intent":"surprise"}"""
        })
        {
            using var refused = await SendAsContact(contact, $"/api/v1/board/cards/{id}/launch", body);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // Refused before any tab exists: the only tab-host call is the middleware's liveness check.
        _tabHost.Verify(host => host.CreateTabAsync(It.IsAny<CancellationToken>()), Times.Never);
        _tabHost.Verify(host => host.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ToolContactLaunch_StartsTheSavedAssignmentWithItsSavedOptionsOnly()
    {
        using var created = await PostJsonAsync("/api/v1/board/cards", new { title = "Contact launch", assignee = "base:codex" });
        created.EnsureSuccessStatusCode();
        using var card = await ReadJsonAsync(created);
        var id = card.RootElement.GetProperty("id").GetString()!;
        var contact = BoundToolContact("contact-tab");
        _tabHost.SetupGet(host => host.MaxTabs).Returns(8);
        _tabHost.Setup(host => host.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("started-tab", DateTime.UtcNow, false));
        StartTerminalRequest? started = null;
        _tabHost.Setup(host => host.StartSessionAsync("started-tab", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, request, _) => started = request)
            .ReturnsAsync(new TerminalStatusResponse(true, Guid.NewGuid().ToString("D"), "codex", _project));

        using var launched = await SendAsContact(contact, $"/api/v1/board/cards/{id}/launch", """{"intent":"work"}""");

        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        Assert.NotNull(started);
        Assert.Equal("codex", started.Cli);
        Assert.NotEqual(true, started.BaseLlmOptions?.Yolo);
    }

    [Fact]
    public void LaunchRequestFor_LeavesTheDashboardRequestWhole()
    {
        var request = new LaunchBoardCardRequest(Intent: "chat", Question: "Why?",
            Review: new ReviewLaunchRequest(Override: new ReviewerTarget("base:claude", new BaseLlmOptions(Yolo: true))));

        Assert.Same(request, BoardRoutes.LaunchRequestFor(new DefaultHttpContext(), request));
        Assert.Null(BoardRoutes.LaunchRequestFor(new DefaultHttpContext(), null));
    }

    /// <summary>A tool contact bound to a live session in <paramref name="tabId"/>, as a started CLI holds.</summary>
    private AgentCredentialService.Contact BoundToolContact(string tabId)
    {
        var session = Guid.NewGuid().ToString("D");
        var contact = _agentCredentials.ForTools(tabId);
        _agentCredentials.BindSession(tabId, session);
        _tabHost.Setup(host => host.ListTabsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TerminalTabStatusResponse(tabId, DateTime.UtcNow, true, session, "claude", _project)]);
        return contact;
    }

    private async Task<HttpResponseMessage> SendAsContact(AgentCredentialService.Contact contact, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
        request.Headers.Add("viberails_session", contact.Session);
        request.Headers.Add("viberails_tab", contact.Tab);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
