using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Auth;
using VibeRails.Middleware;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Routes;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Routes;

public class LaunchRoutesTests
{
    // One client for the class: a client per test leaves its sockets in TIME_WAIT (VIBE-296 N1).
    // Credentials go on each request, never on DefaultRequestHeaders, so the client stays stateless.
    private static readonly HttpClient SharedClient = new();

    private static LaunchSnapshot Snapshot(string project, string? root = null) =>
        new(new LaunchExecutionContext(project, root ?? project, "board", "event", ProjectRoot: root));

    [Fact]
    public async Task BoardCallbackProjectComesOnlyFromTheAuthenticatedSessionAndTab()
    {
        var runs = new Mock<ILaunchStore>();
        // A run queued before VIBE-296 has no recorded root and answers with its project key.
        runs.Setup(x => x.GetActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new LaunchRun("run", 1, "preset", "preset", "authorized-project", "event", Snapshot("authorized-project"), "running", DateTime.UtcNow, SessionId: "session", TabId: "tab")]);
        await using var provider = new ServiceCollection().AddSingleton(runs.Object).BuildServiceProvider();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider };
        http.Items[typeof(AgentCredentialService.Contact)] = new AgentCredentialService.Contact("credential", "credential", "tab", false, "session");
        Assert.Equal("authorized-project", await BoardRoutes.LaunchProjectAsync(http, TestContext.Current.CancellationToken));
        http.Items[typeof(AgentCredentialService.Contact)] = new AgentCredentialService.Contact("credential", "credential", "foreign-tab", false, "session");
        Assert.NotEqual("authorized-project", await BoardRoutes.LaunchProjectAsync(http, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// VIBE-296 item 2: a nested Board launch from a contact-matched run starts in the project as the
    /// queuing root spelled it. The run's ProjectPath is the upper-cased key on Windows and macOS,
    /// which a case-sensitive volume cannot open.
    /// </summary>
    [Fact]
    public async Task NestedBoardLaunchGetsTheCasedProjectRootNotTheComparisonKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "viberails-launch-case-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "Mixed-Case Repo");
        Directory.CreateDirectory(repo);
        try
        {
            var store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
            var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
            var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), ct)).Preset;
            var run = await service.EnqueueAsync("preset", preset.Id, new(repo, repo, "board", "board:case"), ct);
            var claim = (await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, DateTime.UtcNow, ct))!;
            Assert.True(await store.SetTabAsync(claim, "tab", ct));
            Assert.True(await store.StartAsync(claim, "session", null, repo, Environment.ProcessId, DateTime.UtcNow, ct: ct));
            await using var provider = new ServiceCollection().AddSingleton<ILaunchStore>(store).BuildServiceProvider();
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider };
            http.Items[typeof(AgentCredentialService.Contact)] = new AgentCredentialService.Contact("credential", "credential", "tab", false, "session");

            var project = await BoardRoutes.LaunchProjectAsync(http, ct);

            Assert.Equal(LaunchService.ProjectRoot(repo), project, StringComparer.Ordinal);
            Assert.True(Directory.Exists(project));
            var stored = (await store.GetRunAsync(run.Id, ct))!;
            Assert.Equal(LaunchService.ProjectKey(repo), stored.ProjectPath);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                Assert.NotEqual(stored.ProjectPath, project, StringComparer.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("viberails_session", "test-session");
        request.Headers.Add("viberails_tab", "test-tab");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = Authenticated(method, url, body);
        return await SharedClient.SendAsync(request, ct);
    }

    [Fact]
    public async Task CatalogSaveConflictRunAndOwnerBoundAttachmentRoutes()
    {
        var root = Path.Combine(Path.GetTempPath(), "viberails-launch-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(x => x.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        var launches = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        builder.Services.AddSingleton<ILaunchStore>(launches);
        var board = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        builder.Services.AddSingleton<IBoardStore>(board);
        builder.Services.AddSingleton(Mock.Of<IAutomationScriptService>());
        var metadata = new Mock<VibeRails.DB.IRepository>();
        metadata.Setup(x => x.GetKnownProjectPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([root]);
        metadata.Setup(x => x.GetProjectDisplayNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("Project");
        builder.Services.AddSingleton(metadata.Object);
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        builder.Services.AddSingleton(projects.Object);
        builder.Services.AddScoped<LaunchService>();
        // Other Board launch routes bind it; this test never resolves it.
        builder.Services.AddScoped<BoardLaunchWorkflow>(_ => throw new InvalidOperationException("Not used by this test."));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        LaunchRoutes.Map(app);
        BoardLaunchRoutes.Map(app);
        RetiredLaunchRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var url = app.Urls.First();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            foreach (var path in new[] { "/launch-catalog", "/launch-projects", "/presets", "/actions", "/trigger-bindings", "/runs", "/presets/missing/attachments",
                "/runs/missing/attachments/missing/content", "/board/cards/missing/launches/runs" })
            {
                foreach (var credentials in new[] { 0, 1, 2 })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1" + path);
                    if (credentials == 1) request.Headers.Add("viberails_session", "test-session");
                    if (credentials == 2) request.Headers.Add("viberails_tab", "test-tab");
                    using var rejected = await SharedClient.SendAsync(request, ct);
                    Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                }
            }
            using var created = await SendAsync(HttpMethod.Post, url + "/api/v1/presets", new SavePresetRequest("Agent", "", "codex", InitialPrompt: "Work"), ct);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var saved = (await created.Content.ReadFromJsonAsync<SavedPreset>(ct))!;
            using var conflict = await SendAsync(HttpMethod.Put, url + "/api/v1/presets/" + saved.Preset.Id, new SavePresetRequest("Edit", "", "codex", ExpectedRevision: 42), ct);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(1, (await conflict.Content.ReadFromJsonAsync<LaunchError>(ct))!.CurrentRevision);
            using var runResponse = await SendAsync(HttpMethod.Post, url + "/api/v1/runs", new LaunchRequest(saved.Preset.Id), ct);
            Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
            var run = (await runResponse.Content.ReadFromJsonAsync<LaunchRun>(ct))!;
            Assert.Null(run.ClaimToken);
            Assert.True(run.Snapshot.Context.NativeTerminal);
            using var nativeResponse = await SendAsync(HttpMethod.Post, url + "/api/v1/runs",
                new LaunchRequest(saved.Preset.Id, NativeTerminal: true), ct);
            Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);
            var nativeRun = (await nativeResponse.Content.ReadFromJsonAsync<LaunchRun>(ct))!;
            Assert.True(nativeRun.Snapshot.Context.NativeTerminal);
            Assert.Equal(root, nativeRun.Snapshot.Context.WorkingDirectory);
            using var prefixed = await SendAsync(HttpMethod.Post, url + "/api/v1/runs",
                new LaunchRequest(saved.Preset.Id, PromptPrefix: "Fix selected finding", InitialPrompt: "Template value", ExpectedRevision: 1), ct);
            Assert.Equal(HttpStatusCode.OK, prefixed.StatusCode);
            var prefixedRun = (await prefixed.Content.ReadFromJsonAsync<LaunchRun>(ct))!;
            Assert.Equal("Work", prefixedRun.Snapshot.Preset!.InitialPrompt);
            Assert.EndsWith("Fix selected finding\n\nTemplate value", prefixedRun.Snapshot.Context.ComposedPrompt);
            Assert.Equal(root, prefixedRun.Snapshot.Context.WorkingDirectory);
            using var staleRun = await SendAsync(HttpMethod.Post, url + "/api/v1/runs",
                new LaunchRequest(saved.Preset.Id, InitialPrompt: "Stale template value", ExpectedRevision: 99), ct);
            Assert.Equal(HttpStatusCode.Conflict, staleRun.StatusCode);
            using var retiredPrompt = await SendAsync(HttpMethod.Post, url + "/api/v1/runs",
                new LaunchRequest(saved.Preset.Id, InitialPrompt: "{{step:old}}"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, retiredPrompt.StatusCode);
            using var attach = await SendAsync(HttpMethod.Post, url + $"/api/v1/presets/{saved.Preset.Id}/attachments", new LaunchAttachmentRequest("note.txt", "data:text/plain;base64,aGk="), ct);
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
            var attachment = (await attach.Content.ReadFromJsonAsync<LaunchAttachment>(ct))!;
            using var wrongOwner = await SendAsync(HttpMethod.Get, url + $"/api/v1/presets/wrong/attachments/{attachment.Id}/content", null, ct);
            Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);
            using var file = await SendAsync(HttpMethod.Get, url + $"/api/v1/presets/{saved.Preset.Id}/attachments/{attachment.Id}/content", null, ct);
            Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("hi", await file.Content.ReadAsStringAsync(ct));
            using var retired = await SendAsync(HttpMethod.Get, url + "/api/v1/environments", null, ct);
            Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);

            // VIBE-296 item 9: a card's runs come from its own route, whatever else the project ran.
            await board.EnsureDefaultColumnsAsync(root, ct);
            var card = await board.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), ct);
            var other = await board.CreateCardAsync(root, new(null, "Other", "", null, "medium", null, [], false), ct);
            var service = new LaunchService(launches, Mock.Of<IAutomationScriptService>());
            var cardRun = await service.EnqueueAsync("preset", saved.Preset.Id, new(root, root, "manual", "card-run", CardId: card.Id), ct);
            await service.EnqueueAsync("preset", saved.Preset.Id, new(root, root, "manual", "other-run", CardId: other.Id), ct);
            using var cardRuns = await SendAsync(HttpMethod.Get, url + $"/api/v1/board/cards/{card.Key}/launches/runs", null, ct);
            Assert.Equal(HttpStatusCode.OK, cardRuns.StatusCode);
            Assert.Equal("no-store", cardRuns.Headers.CacheControl?.ToString());
            Assert.Equal(cardRun.Id, Assert.Single((await cardRuns.Content.ReadFromJsonAsync<LaunchRunPage>(ct))!.Runs).Id);
            using var pagedPast = await SendAsync(HttpMethod.Get, url + $"/api/v1/board/cards/{card.Id}/launches/runs?offset=50", null, ct);
            Assert.Empty((await pagedPast.Content.ReadFromJsonAsync<LaunchRunPage>(ct))!.Runs);
            using var badOffset = await SendAsync(HttpMethod.Get, url + $"/api/v1/board/cards/{card.Id}/launches/runs?offset=-1", null, ct);
            Assert.Equal(HttpStatusCode.BadRequest, badOffset.StatusCode);
            using var missing = await SendAsync(HttpMethod.Get, url + "/api/v1/board/cards/missing/launches/runs", null, ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var scopeSave = await SendAsync(HttpMethod.Put, url + "/api/v1/presets/" + saved.Preset.Id,
                new SavePresetRequest("Scoped", "", "codex", InitialPrompt: "Work", ExpectedRevision: 1, ProjectPaths: [root + "-elsewhere"]), ct);
            Assert.Equal(HttpStatusCode.OK, scopeSave.StatusCode);
            using var catalog = await SendAsync(HttpMethod.Get, url + "/api/v1/launch-catalog", null, ct);
            Assert.Empty((await catalog.Content.ReadFromJsonAsync<LaunchCatalog>(ct))!.Items);
            using var management = await SendAsync(HttpMethod.Get, url + "/api/v1/launch-catalog?includeAll=true", null, ct);
            Assert.False(Assert.Single((await management.Content.ReadFromJsonAsync<LaunchCatalog>(ct))!.Items).CanLaunch);
            using var blocked = await SendAsync(HttpMethod.Post, url + "/api/v1/runs", new LaunchRequest(saved.Preset.Id), ct);
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Contains("not available in this project", (await blocked.Content.ReadFromJsonAsync<LaunchError>(ct))!.Error);
            using var known = await SendAsync(HttpMethod.Get, url + "/api/v1/launch-projects", null, ct);
            Assert.True(Assert.Single((await known.Content.ReadFromJsonAsync<LaunchProject[]>(ct))!).IsCurrent);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            Directory.Delete(root, true);
        }
    }
}
