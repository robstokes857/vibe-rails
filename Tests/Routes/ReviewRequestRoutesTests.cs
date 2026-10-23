using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.VcaRegression;
using VibeRails.Auth;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Review-request routes through the real middleware and a real state.db; the staged snapshot
/// and viberails.ai are fakes. One client per class: per-test clients leave sockets in TIME_WAIT.
/// </summary>
[Collection("ProcessEnvIsolation")] // mutates ParserConfigs.SetGitState (process-global), like BoardRoutesTests
public sealed class ReviewRequestRoutesTests : IAsyncLifetime
{
    private static readonly HttpClient SharedClient = new();
    private static readonly string Tree = new('b', 40);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-review-routes-{Guid.NewGuid():N}");
    private readonly string _originalRootPath = ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = ParserConfigs.GetIsInGit();
    private readonly FakeCommitSignatureVerifier _verifier = FakeCommitSignatureVerifier.ValidFor("lead@example.com");
    private string _project = null!;
    private WebApplication _app = null!;
    private Uri _baseUri = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_project);
        ParserConfigs.SetGitState(_project, isInGit: true);
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state.db"), Pooling = false }.ToString();

        var snapshots = new Mock<IGitStagedSnapshotProvider>(MockBehavior.Strict);
        snapshots.Setup(s => s.CaptureAsync(_project, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStagedSnapshot(
            _project,
            [new GitStagedFileSnapshot("db.cs", Path.Combine(_project, "db.cs"), GitStagedChangeKind.Modified, true, false, 1,
                "class Db { int Version = 2; }\n", PreviousContent: "class Db { }\n")],
            [],
            StagedIdentity: new GitStagedSnapshotIdentity(new string('c', 40), new string('d', 40), Tree)));
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(sessions.Object);
        builder.Services.AddSingleton(new ReviewRequestService(new ReviewRequestStore(connection), snapshots.Object, _verifier));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        _app = builder.Build();
        _app.UseMiddleware<CookieAuthMiddleware>();
        ReviewRequestRoutes.Map(_app);
        await _app.StartAsync(Ct);
        _baseUri = new Uri(_app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        ParserConfigs.SetGitState(_originalRootPath, _originalIsInGit);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task EveryRouteNeedsBothCredentials()
    {
        var id = Guid.NewGuid();
        (HttpMethod Method, string Path)[] routes = [
            (HttpMethod.Get, "/api/v1/review-requests"), (HttpMethod.Post, "/api/v1/review-requests"),
            (HttpMethod.Get, $"/api/v1/review-requests/{id}"), (HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions"),
            (HttpMethod.Post, $"/api/v1/review-requests/{id}/cancel"), (HttpMethod.Delete, $"/api/v1/review-requests/{id}")];
        foreach (var (method, path) in routes)
        foreach (var credentials in new[] { 0, 1, 2 })
        {
            using var denied = await Send(method, path, "{}", credentials);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }

    [Fact]
    public async Task BodiesAreBoundedTypedAndParsed()
    {
        using var notJson = await Send(HttpMethod.Post, "/api/v1/review-requests", "reviewers=x", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);
        using var oversized = await Send(HttpMethod.Post, "/api/v1/review-requests", new string(' ', ReviewRequestRoutes.MaxCreateBodyBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var broken = await Send(HttpMethod.Post, "/api/v1/review-requests", "{not json");
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("invalid_request", await CodeAsync(broken));
        using var noReviewers = await Send(HttpMethod.Post, "/api/v1/review-requests", "{\"reviewers\":[]}");
        Assert.Equal(HttpStatusCode.BadRequest, noReviewers.StatusCode);
        Assert.Equal("invalid_request", await CodeAsync(noReviewers));
        Assert.True(noReviewers.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CreateReadDecideCancelAndDeleteRoundTrip()
    {
        using var created = await Send(HttpMethod.Post, "/api/v1/review-requests",
            "{\"reviewers\":[\"lead@example.com\"],\"cardKey\":\"VB-50\",\"note\":\"first pass\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("open", createdJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(Tree, createdJson.RootElement.GetProperty("treeId").GetString());
        Assert.Equal(1, createdJson.RootElement.GetProperty("fileCount").GetInt32());

        using var list = await Send(HttpMethod.Get, "/api/v1/review-requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        Assert.Equal(id, Assert.Single(listJson.RootElement.GetProperty("requests").EnumerateArray()).GetProperty("id").GetGuid());

        using var detail = await Send(HttpMethod.Get, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct));
        var file = Assert.Single(detailJson.RootElement.GetProperty("bundle").GetProperty("files").EnumerateArray());
        Assert.Equal("db.cs", file.GetProperty("path").GetString());
        Assert.Equal("class Db { }\n", file.GetProperty("before").GetString());
        Assert.Equal("first pass", detailJson.RootElement.GetProperty("bundle").GetProperty("note").GetString());

        using var wrongTree = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "approved", trailer = HumanSignatureRuleTests.SignatureLine(new string('0', 40)) }));
        Assert.Equal(HttpStatusCode.Conflict, wrongTree.StatusCode);
        Assert.Equal("tree_mismatch", await CodeAsync(wrongTree));
        Assert.Empty(_verifier.Calls);

        using var approved = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "approved", trailer = HumanSignatureRuleTests.SignatureLine(Tree), comment = "fine" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(Ct));
        Assert.Equal("approved", approvedJson.RootElement.GetProperty("status").GetString());
        var decision = Assert.Single(approvedJson.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("lead@example.com", decision.GetProperty("reviewer").GetString());
        Assert.Equal(HumanSignatureRuleTests.SignatureLine(Tree), decision.GetProperty("trailer").GetString());
        Assert.Single(_verifier.Calls);

        using var cancelled = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var cancelledJson = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync(Ct));
        Assert.Equal("cancelled", cancelledJson.RootElement.GetProperty("status").GetString());

        using var deleted = await Send(HttpMethod.Delete, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await Send(HttpMethod.Get, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("not_found", await CodeAsync(gone));
        using var unknown = await Send(HttpMethod.Get, $"/api/v1/review-requests/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("code").GetString();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? json = null, int credentials = 3,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        if ((credentials & 1) != 0) request.Headers.Add("viberails_session", "test-session");
        if ((credentials & 2) != 0) request.Headers.Add("viberails_tab", "test-tab");
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, contentType);
        return await SharedClient.SendAsync(request, Ct);
    }
}
