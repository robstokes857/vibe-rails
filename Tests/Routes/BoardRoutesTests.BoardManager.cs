using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task AiBoardManagerSwitch_IsOffByDefault_NeedsBothCredentials_AndRefusesAMissingValue()
    {
        const string path = "/api/v1/board/ai-board-manager";
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Get, path, credentials.Session, credentials.Tab);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var initial = await GetJsonAsync(path);
        Assert.False(initial.RootElement.GetProperty("enabled").GetBoolean());

        using var on = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerFeatureRequest(true));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using var onJson = await ReadJsonAsync(on);
        Assert.True(onJson.RootElement.GetProperty("enabled").GetBoolean());
        using var missing = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerFeatureRequest());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var reread = await GetJsonAsync(path);
        Assert.True(reread.RootElement.GetProperty("enabled").GetBoolean());
        using var deniedWrite = await SendAsync(HttpMethod.Put, path, "test-session", null, new UpdateBoardManagerFeatureRequest(false));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedWrite.StatusCode);
    }

    [Fact]
    public async Task BoardManagerRoutes_WithTheSwitchOff_ReportOff_AndRefuseToSaveOrStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var boardId = (await store.GetBoardsAsync(_project, ct)).Single().Id;
        var path = $"/api/v1/board/boards/{boardId}/manager";
        _tabHost.Setup(host => host.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        using var off = await GetJsonAsync(path);
        Assert.False(off.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, off.RootElement.GetProperty("selection").ValueKind);
        using var save = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerRequest("base:codex", null, 0));
        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);
        using var saveJson = await ReadJsonAsync(save);
        Assert.Equal(BoardManagerService.DisabledMessage, saveJson.RootElement.GetProperty("error").GetString());
        using var launch = await PostJsonAsync(path + "/launch", new { });
        Assert.Equal(HttpStatusCode.Conflict, launch.StatusCode);
        // Stopping is always allowed; there is nothing to stop here.
        using var stop = await PostJsonAsync(path + "/stop", new { });
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var stopJson = await ReadJsonAsync(stop);
        Assert.False(stopJson.RootElement.GetProperty("stopped").GetBoolean());
        // Agent cards need their own manager, exactly as before board managers.
        using var agentCard = await PostJsonAsync("/api/v1/board/cards", new { title = "Needs a manager", workflowMode = "agent" });
        Assert.Equal(HttpStatusCode.BadRequest, agentCard.StatusCode);
    }

    [Fact]
    public async Task BoardManagerRoutesSaveWithRevision_LaunchOnce_Stop_AndLetAgentCardsRelyOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        await store.SetBoardManagerEnabledAsync(true, ct);
        var boardId = (await store.GetBoardsAsync(_project, ct)).Single().Id;
        var path = $"/api/v1/board/boards/{boardId}/manager";
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Get, path, credentials.Session, credentials.Tab);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var liveTabs = new List<TerminalTabStatusResponse>();
        var requests = new List<StartTerminalRequest>();
        _tabHost.SetupGet(host => host.MaxTabs).Returns(8);
        _tabHost.Setup(host => host.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => liveTabs.ToList());
        using var empty = await GetJsonAsync(path);
        Assert.True(empty.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(empty.RootElement.GetProperty("selection").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.Equal(0, empty.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal(1, empty.RootElement.GetProperty("maxWorkers").GetInt32());
        using var noManagerYet = await PostJsonAsync("/api/v1/board/cards", new { title = "Needs a manager", workflowMode = "agent" });
        Assert.Equal(HttpStatusCode.BadRequest, noManagerYet.StatusCode);

        using var saved = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab",
            new UpdateBoardManagerRequest("base:codex", new BaseLlmOptions(Model: "gpt-6-astra", Speed: "ultrafast"), 0, "Bugs first.", 2));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedJson = await ReadJsonAsync(saved);
        Assert.Equal("base:codex", savedJson.RootElement.GetProperty("selection").GetString());
        Assert.Equal(1, savedJson.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal("ultrafast", savedJson.RootElement.GetProperty("baseLlmOptions").GetProperty("speed").GetString());
        Assert.Equal("Bugs first.", savedJson.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(2, savedJson.RootElement.GetProperty("maxWorkers").GetInt32());
        // Omitted standing orders and limit keep what is saved.
        using var kept = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerRequest("base:codex", null, 1));
        using var keptJson = await ReadJsonAsync(kept);
        Assert.Equal("Bugs first.", keptJson.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(2, keptJson.RootElement.GetProperty("maxWorkers").GetInt32());
        using var stale = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerRequest("base:claude", null, 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var noRevision = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerRequest("base:claude"));
        Assert.Equal(HttpStatusCode.BadRequest, noRevision.StatusCode);
        using var tooMany = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardManagerRequest("base:codex", null, 2, MaxWorkers: 9));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        using var foreign = await SendAsync(HttpMethod.Get, "/api/v1/board/boards/board_missing/manager", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using var managedCard = await PostJsonAsync("/api/v1/board/cards", new { title = "Board managed", workflowMode = "agent", assignee = "base:claude" });
        Assert.Equal(HttpStatusCode.OK, managedCard.StatusCode);
        using var managedJson = await ReadJsonAsync(managedCard);
        var cardId = managedJson.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, managedJson.RootElement.GetProperty("managerSelection").ValueKind);

        _tabHost.Setup(host => host.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("manager-tab", DateTime.UtcNow, false));
        _tabHost.Setup(host => host.StartSessionAsync("manager-tab", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, request, _) => requests.Add(request))
            .ReturnsAsync(() =>
            {
                var session = new TerminalStatusResponse(true, Guid.NewGuid().ToString("D"), "codex", _project);
                liveTabs.Add(new TerminalTabStatusResponse("manager-tab", DateTime.UtcNow, true, session.SessionId));
                return session;
            });
        _tabHost.Setup(host => host.DeleteTabAsync("manager-tab", It.IsAny<CancellationToken>()))
            .Callback(() => liveTabs.RemoveAll(tab => tab.TabId == "manager-tab"))
            .ReturnsAsync(true);
        using var launched = await PostJsonAsync(path + "/launch", new { });
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        using var launchedJson = await ReadJsonAsync(launched);
        Assert.False(launchedJson.RootElement.GetProperty("reused").GetBoolean());
        Assert.Equal("manager-tab", launchedJson.RootElement.GetProperty("tabId").GetString());
        var request = Assert.Single(requests);
        Assert.Equal("codex", request.Cli); Assert.Equal("ultrafast", request.BaseLlmOptions!.Speed);
        Assert.StartsWith("You are the AI board manager for the VibeRails board", request.InitialPrompt);
        Assert.Contains("Board managed", request.InitialPrompt);
        Assert.Contains("Run at most 2 implementation workers at a time", request.InitialPrompt);

        using var status = await GetJsonAsync(path);
        Assert.Equal("manager-tab", status.RootElement.GetProperty("active").GetProperty("tabId").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("managedCards").GetInt32());
        Assert.Equal(0, status.RootElement.GetProperty("runningWorkers").GetInt32());
        using var again = await PostJsonAsync(path + "/launch", new { });
        using var againJson = await ReadJsonAsync(again);
        Assert.True(againJson.RootElement.GetProperty("reused").GetBoolean());
        Assert.Single(requests);

        // Start Agent workflow on the managed card reaches the same manager, and the card shows it.
        using var cardLaunch = await PostJsonAsync($"/api/v1/board/cards/{cardId}/launch", new { intent = "manager" });
        Assert.Equal(HttpStatusCode.OK, cardLaunch.StatusCode);
        Assert.Single(requests);
        using var detail = await GetJsonAsync($"/api/v1/board/cards/{cardId}");
        var session = Assert.Single(detail.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal("agent_manager", session.GetProperty("origin").GetString());
        Assert.Equal(launchedJson.RootElement.GetProperty("sessionId").GetString(), session.GetProperty("id").GetString());
        using var deniedLaunch = await SendAsync(HttpMethod.Post, path + "/launch", "test-session");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLaunch.StatusCode);
        using var deniedStop = await SendAsync(HttpMethod.Post, path + "/stop", "test-session");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedStop.StatusCode);

        using var stop = await PostJsonAsync(path + "/stop", new { });
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var stopJson = await ReadJsonAsync(stop);
        Assert.True(stopJson.RootElement.GetProperty("stopped").GetBoolean());
        Assert.Equal(launchedJson.RootElement.GetProperty("sessionId").GetString(), stopJson.RootElement.GetProperty("sessionId").GetString());
        using var stopped = await GetJsonAsync(path);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, stopped.RootElement.GetProperty("active").ValueKind);
    }
}
