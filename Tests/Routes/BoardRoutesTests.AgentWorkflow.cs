using System.Net;
using Moq;
using VibeRails.DTOs;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task AgentWorkflowRoutesSaveManagerAndDelegateWorkerWithoutReassigning()
    {
        using var missingManager = await PostJsonAsync("/api/v1/board/cards", new { title = "Invalid", workflowMode = "agent" });
        Assert.Equal(HttpStatusCode.BadRequest, missingManager.StatusCode);
        using var created = await PostJsonAsync("/api/v1/board/cards", new
        {
            title = "Managed pipeline", workflowMode = "agent", managerSelection = "base:codex", assignee = "base:claude",
            managerBaseLlmOptions = new { model = "gpt-6-astra", speed = "ultrafast" }
        });
        using var saved = await ReadJsonAsync(created);
        var id = saved.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("agent", saved.RootElement.GetProperty("workflowMode").GetString());
        Assert.Equal("ultrafast", saved.RootElement.GetProperty("managerBaseLlmOptions").GetProperty("speed").GetString());

        _tabHost.SetupGet(host => host.MaxTabs).Returns(8);
        _tabHost.Setup(host => host.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tabHost.Setup(host => host.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("agent-tab", DateTime.UtcNow, false));
        var requests = new List<StartTerminalRequest>();
        _tabHost.Setup(host => host.StartSessionAsync("agent-tab", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, request, _) => requests.Add(request))
            .ReturnsAsync(() => new TerminalStatusResponse(true, Guid.NewGuid().ToString("D"), "codex", _project));
        using var managerLaunch = await PostJsonAsync($"/api/v1/board/cards/{id}/launch", new { intent = "manager" });
        Assert.Equal(HttpStatusCode.OK, managerLaunch.StatusCode);
        Assert.Equal("codex", requests[0].Cli);
        Assert.Equal("ultrafast", requests[0].BaseLlmOptions!.Speed);
        Assert.Contains("manager agent for this card", requests[0].InitialPrompt);
        using var workerLaunch = await PostJsonAsync($"/api/v1/board/cards/{id}/launch", new { intent = "worker", selection = "base:claude", task = "Implement the feature and report validation." });
        Assert.Equal(HttpStatusCode.OK, workerLaunch.StatusCode);
        Assert.Equal("claude", requests[1].Cli);
        Assert.Contains("Implement the feature and report validation.", requests[1].InitialPrompt);
        using var detail = await GetJsonAsync($"/api/v1/board/cards/{id}");
        Assert.Equal("base:codex", detail.RootElement.GetProperty("managerSelection").GetString());
        Assert.Equal("base:claude", detail.RootElement.GetProperty("assignee").GetString());
        var origins = detail.RootElement.GetProperty("sessions").EnumerateArray().Select(session => session.GetProperty("origin").GetString()).ToList();
        Assert.Contains("agent_manager", origins);
        Assert.Contains("agent_worker", origins);
        using var denied = await SendAsync(HttpMethod.Post, $"/api/v1/board/cards/{id}/launch", "test-session", null, new LaunchBoardCardRequest(Intent: "worker"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(2, requests.Count);
    }
}
