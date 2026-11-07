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

    [Fact]
    public async Task BoardCallbackProjectComesOnlyFromTheAuthenticatedSessionAndTab()
    {
        var runs = new Mock<ILaunchStore>();
        runs.Setup(x => x.GetActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new LaunchRun("run", 1, "preset", "preset", "authorized-project", "event", null!, "running", DateTime.UtcNow, SessionId: "session", TabId: "tab")]);
        await using var provider = new ServiceCollection().AddSingleton(runs.Object).BuildServiceProvider();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider };
        http.Items[typeof(AgentCredentialService.Contact)] = new AgentCredentialService.Contact("credential", "credential", "tab", false, "session");
        Assert.Equal("authorized-project", await BoardRoutes.LaunchProjectAsync(http, TestContext.Current.CancellationToken));
        http.Items[typeof(AgentCredentialService.Contact)] = new AgentCredentialService.Contact("credential", "credential", "foreign-tab", false, "session");
        Assert.NotEqual("authorized-project", await BoardRoutes.LaunchProjectAsync(http, TestContext.Current.CancellationToken));
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
        builder.Services.AddSingleton<ILaunchStore>(new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False"));
        builder.Services.AddSingleton(Mock.Of<IAutomationScriptService>());
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        builder.Services.AddSingleton(projects.Object);
        builder.Services.AddScoped<LaunchService>();
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        LaunchRoutes.Map(app);
        RetiredLaunchRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var url = app.Urls.First();
        var ct = TestContext.Current.CancellationToken;
        using var Client = new HttpClient();
        try
        {
            foreach (var path in new[] { "/launch-catalog", "/presets", "/actions", "/trigger-bindings", "/runs", "/presets/missing/attachments", "/runs/missing/attachments/missing/content" })
            {
                foreach (var credentials in new[] { 0, 1, 2 })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1" + path);
                    if (credentials == 1) request.Headers.Add("viberails_session", "test-session");
                    if (credentials == 2) request.Headers.Add("viberails_tab", "test-tab");
                    using var rejected = await Client.SendAsync(request, ct);
                    Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                }
            }
            Client.DefaultRequestHeaders.Add("viberails_session", "test-session");
            Client.DefaultRequestHeaders.Add("viberails_tab", "test-tab");
            using var created = await Client.PostAsJsonAsync(url + "/api/v1/presets", new SavePresetRequest("Agent", "", "codex", InitialPrompt: "Work"), ct);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var saved = (await created.Content.ReadFromJsonAsync<SavedPreset>(ct))!;
            using var conflict = await Client.PutAsJsonAsync(url + "/api/v1/presets/" + saved.Preset.Id, new SavePresetRequest("Edit", "", "codex", ExpectedRevision: 42), ct);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(1, (await conflict.Content.ReadFromJsonAsync<LaunchError>(ct))!.CurrentRevision);
            using var runResponse = await Client.PostAsJsonAsync(url + "/api/v1/runs", new LaunchRequest(saved.Preset.Id), ct);
            Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
            var run = (await runResponse.Content.ReadFromJsonAsync<LaunchRun>(ct))!;
            Assert.Null(run.ClaimToken);
            using var attach = await Client.PostAsJsonAsync(url + $"/api/v1/presets/{saved.Preset.Id}/attachments", new LaunchAttachmentRequest("note.txt", "data:text/plain;base64,aGk="), ct);
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
            var attachment = (await attach.Content.ReadFromJsonAsync<LaunchAttachment>(ct))!;
            using var wrongOwner = await Client.GetAsync(url + $"/api/v1/presets/wrong/attachments/{attachment.Id}/content", ct);
            Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);
            using var file = await Client.GetAsync(url + $"/api/v1/presets/{saved.Preset.Id}/attachments/{attachment.Id}/content", ct);
            Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("hi", await file.Content.ReadAsStringAsync(ct));
            using var retired = await Client.GetAsync(url + "/api/v1/environments", ct);
            Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            Directory.Delete(root, true);
        }
    }
}
