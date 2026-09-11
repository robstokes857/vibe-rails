using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// /api/v1/board/* behind the real CookieAuthMiddleware and the AOT JSON context, over a real
/// temp store. Git and the terminal tab host are faked; the launch test asserts what reaches the
/// tab host (the composed InitialPrompt) and what the board records (the session link).
/// </summary>
[Collection("ProcessEnvIsolation")] // mutates ParserConfigs.SetGitState (process-global), like AutomationNavPreferenceServiceTests
public sealed class BoardRoutesTests : IAsyncLifetime
{
    // One client for the whole class: a per-test HttpClient leaves sockets in TIME_WAIT.
    private static readonly HttpClient SharedClient = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-routes-{Guid.NewGuid():N}");
    private readonly string _originalRootPath = ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = ParserConfigs.GetIsInGit();
    private readonly Mock<ITerminalTabHostService> _tabHost = new(MockBehavior.Strict);
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private string _connectionString = null!;
    private string _project = null!;
    private WebApplication _app = null!;
    private Uri _baseUri = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _project = BoardStore.NormalizeProjectPath(Path.Combine(_root, "project"));
        Directory.CreateDirectory(_project);
        ParserConfigs.SetGitState(_project, isInGit: true);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

        var commits = new Mock<IBoardCommitService>(MockBehavior.Strict);
        commits.Setup(c => c.DescribeAsync(It.IsAny<string>(), "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo("abc1234abc1234abc1234abc1234abc1234abc12", "Rob", "Fix", DateTime.UtcNow));
        commits.Setup(c => c.GetDiffAsync(It.IsAny<string>(), "abc1234abc1234abc1234abc1234abc1234abc12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([new BoardCommitDiffFile("a.cs", "csharp", "old", "new")], 1));
        builder.Services.AddSingleton<IBoardStore>(new BoardStore(_connectionString));
        builder.Services.AddSingleton(commits.Object);
        builder.Services.AddSingleton<IBoardLiveSessionProbe, NullBoardLiveSessionProbe>();
        builder.Services.AddScoped<IBoardService, BoardService>();
        builder.Services.AddSingleton(_tabHost.Object);
        builder.Services.AddSingleton(_repository.Object);
        builder.Services.AddScoped<IBoardLaunchService, BoardLaunchService>();

        _app = builder.Build();
        _app.UseMiddleware<CookieAuthMiddleware>();
        BoardRoutes.Map(_app);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _baseUri = new Uri(_app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        ParserConfigs.SetGitState(_originalRootPath, _originalIsInGit);
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task EveryBoardRoute_NeedsBothCredentials()
    {
        using var none = await SendAsync(HttpMethod.Get, "/api/v1/board/cards");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        using var sessionOnly = await SendAsync(HttpMethod.Get, "/api/v1/board/cards", session: "test-session");
        Assert.Equal(HttpStatusCode.Unauthorized, sessionOnly.StatusCode);
        using var both = await SendAsync(HttpMethod.Get, "/api/v1/board/cards", session: "test-session", tab: "test-tab");
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
    }

    [Fact]
    public async Task Columns_Cards_Comments_Commits_RoundTrip_ThroughTheJsonContext()
    {
        using var columnsDocument = await GetJsonAsync("/api/v1/board/columns");
        var columns = columnsDocument.RootElement.GetProperty("columns");
        Assert.Equal(5, columns.GetArrayLength());
        var backlogId = columns[0].GetProperty("id").GetString()!;
        var reviewId = columns[3].GetProperty("id").GetString()!;
        Assert.Equal(8, columns[1].GetProperty("wipLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, columns[0].GetProperty("wipLimit").ValueKind);

        // Create: the wire shape board-api.js sends (points "" clears, tags array).
        using var created = await PostJsonAsync("/api/v1/board/cards", new
        {
            columnId = backlogId, title = " Fix it ", description = "Body", assignee = "base:claude",
            priority = "high", points = "5", tags = new[] { "auth" }, blocked = false
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var card = createdDocument.RootElement;
        var cardId = card.GetProperty("id").GetString()!;
        Assert.Equal("VB-1", card.GetProperty("key").GetString());
        Assert.Equal("Fix it", card.GetProperty("title").GetString());
        Assert.Equal("base:claude", card.GetProperty("assignee").GetString());
        Assert.Equal(5, card.GetProperty("points").GetInt32());
        Assert.Equal(0, card.GetProperty("commentCount").GetInt32());
        Assert.Equal(0, card.GetProperty("comments").GetArrayLength());
        Assert.Equal(0, card.GetProperty("sessions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("activeTabId").ValueKind);
        Assert.True(card.TryGetProperty("createdAt", out _));

        // Validation errors come back as { error } with 400.
        using var bad = await PostJsonAsync("/api/v1/board/cards", new { title = "x", priority = "urgent" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var badDocument = await ReadJsonAsync(bad);
        Assert.Contains("Priority must be one of", badDocument.RootElement.GetProperty("error").GetString());

        // Update by key with points "" → cleared; move by id; comment as the user.
        using var updated = await SendJsonAsync(HttpMethod.Put, "/api/v1/board/cards/VB-1", new { points = "", tags = Array.Empty<string>() });
        using var updatedDocument = await ReadJsonAsync(updated);
        Assert.Equal(JsonValueKind.Null, updatedDocument.RootElement.GetProperty("points").ValueKind);
        Assert.Equal(0, updatedDocument.RootElement.GetProperty("tags").GetArrayLength());

        using var moved = await PostJsonAsync($"/api/v1/board/cards/{cardId}/move", new { columnId = reviewId, position = 0 });
        using var movedDocument = await ReadJsonAsync(moved);
        Assert.Equal(reviewId, movedDocument.RootElement.GetProperty("columnId").GetString());

        using var commented = await PostJsonAsync($"/api/v1/board/cards/{cardId}/comments", new { body = "Looks good" });
        using var commentDocument = await ReadJsonAsync(commented);
        Assert.Equal("user", commentDocument.RootElement.GetProperty("author").GetProperty("kind").GetString());
        Assert.Equal("You", commentDocument.RootElement.GetProperty("author").GetProperty("label").GetString());

        using var linked = await PostJsonAsync($"/api/v1/board/cards/{cardId}/commits", new { sha = "abc1234" });
        using var linkedDocument = await ReadJsonAsync(linked);
        Assert.Equal("abc1234", linkedDocument.RootElement.GetProperty("shortSha").GetString());
        using var snapshot = await GetJsonAsync($"/api/v1/board/cards/{cardId}/commits/abc1234/diff");
        var changedFile = Assert.Single(snapshot.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("old", changedFile.GetProperty("originalContent").GetString());
        Assert.Equal("new", changedFile.GetProperty("modifiedContent").GetString());

        using var list = await GetJsonAsync("/api/v1/board/cards");
        var summary = Assert.Single(list.RootElement.GetProperty("cards").EnumerateArray());
        Assert.Equal(1, summary.GetProperty("commentCount").GetInt32());
        Assert.False(summary.TryGetProperty("comments", out _)); // summaries carry no rails

        using var missing = await SendAsync(HttpMethod.Get, "/api/v1/board/cards/VB-42", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Deleting the last lane is a conflict, not a 500.
        for (var index = columns.GetArrayLength() - 1; index > 0; index--)
        {
            using var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/board/columns/{columns[index].GetProperty("id").GetString()}", "test-session", "test-tab");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
        using var last = await SendAsync(HttpMethod.Delete, $"/api/v1/board/columns/{backlogId}", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
    }

    [Fact]
    public async Task Launch_ComposesThePrompt_StartsATab_AndLinksTheSession()
    {
        using var columnsDocument = await GetJsonAsync("/api/v1/board/columns");
        using var created = await PostJsonAsync("/api/v1/board/cards", new { title = "Ship the board", description = "All of it.", assignee = "env:7:codex" });
        using var createdDocument = await ReadJsonAsync(created);
        var cardId = createdDocument.RootElement.GetProperty("id").GetString()!;

        // Unassigned launch without an override → 400 with a readable message.
        using var unassignedCreate = await PostJsonAsync("/api/v1/board/cards", new { title = "Nobody" });
        using var unassignedDocument = await ReadJsonAsync(unassignedCreate);
        using var unassignedLaunch = await PostJsonAsync($"/api/v1/board/cards/{unassignedDocument.RootElement.GetProperty("id").GetString()}/launch", new { });
        Assert.Equal(HttpStatusCode.BadRequest, unassignedLaunch.StatusCode);

        _repository.Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLM_Environment { Id = 7, LLM = LLM.Codex, CustomName = "my-env", CustomPrompt = "Read AGENTS.md. {{datetime}}", ProjectPath = _project });
        _tabHost.SetupGet(t => t.MaxTabs).Returns(8);
        _tabHost.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tabHost.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("tab-1", DateTime.UtcNow, false));
        StartTerminalRequest? started = null;
        _tabHost.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, request, _) => started = request)
            .ReturnsAsync(new TerminalStatusResponse(true, "sess-1", "codex", _project));

        using var launched = await PostJsonAsync($"/api/v1/board/cards/VB-1/launch", new { });
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        using var launchDocument = await ReadJsonAsync(launched);
        Assert.Equal("tab-1", launchDocument.RootElement.GetProperty("tabId").GetString());
        Assert.Equal("sess-1", launchDocument.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("VB-1", launchDocument.RootElement.GetProperty("cardKey").GetString());
        Assert.Equal("env:7:codex", launchDocument.RootElement.GetProperty("selection").GetString());

        Assert.NotNull(started);
        Assert.Equal("codex", started!.Cli);
        Assert.Equal("my-env", started.EnvironmentName);
        Assert.Equal(_project, started.WorkingDirectory);
        Assert.Equal("VB-1 · Ship the board", started.Title);
        Assert.StartsWith("You are working on kanban card VB-1", started.InitialPrompt);
        Assert.Contains("Title: Ship the board\nAll of it.", started.InitialPrompt);
        // The environment's template is appended unresolved — resolution happens once, in the tab child.
        Assert.EndsWith("\n\nRead AGENTS.md. {{datetime}}", started.InitialPrompt);

        using var detail = await GetJsonAsync($"/api/v1/board/cards/{cardId}");
        var session = Assert.Single(detail.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal("sess-1", session.GetProperty("id").GetString());
        Assert.Equal("tab-1", session.GetProperty("tabId").GetString());
        Assert.Equal("launch", session.GetProperty("origin").GetString());
        Assert.Equal("env:7:codex", session.GetProperty("selection").GetString());
        Assert.False(session.GetProperty("active").GetBoolean());

        // A stale environment id is a 400, never a fall back by name.
        _repository.Setup(r => r.GetEnvironmentByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync((LLM_Environment?)null);
        using var stale = await PostJsonAsync($"/api/v1/board/cards/VB-1/launch", new { selection = "env:9:claude" });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        // A second browser with an old card view must not start another known-live agent.
        _tabHost.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TerminalTabStatusResponse("tab-1", DateTime.UtcNow, true, "sess-1", "codex", _project)]);
        using var duplicate = await PostJsonAsync("/api/v1/board/cards/VB-1/launch", new { });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var duplicateBody = await ReadJsonAsync(duplicate);
        Assert.Contains("already running", duplicateBody.RootElement.GetProperty("error").GetString());
        _tabHost.Verify(t => t.CreateTabAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, "test-session", "test-tab");
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body) => SendJsonAsync(HttpMethod.Post, path, body);

    private async Task<HttpResponseMessage> SendJsonAsync<T>(HttpMethod method, string path, T body)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.Add("viberails_session", "test-session");
        request.Headers.Add("viberails_tab", "test-tab");
        request.Content = JsonContent.Create(body);
        return await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? session = null, string? tab = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        if (session != null) request.Headers.Add("viberails_session", session);
        if (tab != null) request.Headers.Add("viberails_tab", tab);
        return await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
