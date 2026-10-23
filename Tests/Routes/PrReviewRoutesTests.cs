using System.Net;
using System.Text;
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
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Routes;

public sealed class PrReviewRoutesTests : IAsyncLifetime
{
    // One client per class: a client per test leaves sockets in TIME_WAIT across the suite.
    private static readonly HttpClient SharedClient = new();
    private static readonly GitHubRepository Repo = new("octo", "widgets");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"viberails-pr-review-routes-{Guid.NewGuid():N}.db");
    private readonly Mock<IGitHubPullRequestClient> _github = new();
    private readonly Mock<IPrReviewRepositoryResolver> _repositories = new();
    private WebApplication _app = null!;
    private string _base = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The route derives the project from the dashboard root; the resolver stands in for git.
        _repositories.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new PrReviewProject(path, Repo, null));
        var store = new PrReviewStore($"Data Source={_dbPath};Mode=ReadWriteCreate",
            legacyConnectionString: $"Data Source={_dbPath}.legacy;Mode=ReadWriteCreate");
        await store.SetReviewerSettingsAsync(true, Ct);
        builder.Services.AddSingleton<IPrReviewStore>(store);
        builder.Services.AddSingleton(_github.Object);
        builder.Services.AddSingleton(_repositories.Object);
        builder.Services.AddScoped<PrReviewService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        _app = builder.Build();
        _app.UseMiddleware<CookieAuthMiddleware>();
        PrReviewRoutes.Map(_app);
        await _app.StartAsync(Ct);
        _base = _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate"));
        try { File.Delete(_dbPath); } catch (IOException) { }
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_dbPath}.legacy;Mode=ReadWriteCreate"));
        try { File.Delete(_dbPath + ".legacy"); } catch (IOException) { }
    }

    [Fact]
    public async Task Every_route_requires_both_credentials()
    {
        using var none = await Send(HttpMethod.Get, "/api/v1/pr-reviews", session: false, tab: false);
        using var sessionOnly = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start", "{}", tab: false);
        using var resolution = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/legacy-conflict/resolve", "{}", session: false, tab: false);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sessionOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resolution.StatusCode);
        _repositories.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("GET", "/api/v1/pr-reviews/settings", null)]
    [InlineData("PUT", "/api/v1/pr-reviews/settings", "{\"enabled\":false}")]
    [InlineData("POST", "/api/v1/pr-reviews/7/reviews/1/publish", null)]
    public async Task Reviewer_settings_and_publish_require_both_credentials(string method, string path, string? json)
    {
        foreach (var (session, tab) in new[] { (false, false), (true, false), (false, true) })
        {
            using var response = await Send(new HttpMethod(method), path, json, session: session, tab: tab);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Settings_round_trip_and_disable_new_review_work()
    {
        using var save = await Send(HttpMethod.Put, "/api/v1/pr-reviews/settings", "{\"enabled\":false}");
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var read = await Send(HttpMethod.Get, "/api/v1/pr-reviews/settings");
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Contains("no-store", read.Headers.CacheControl!.ToString());
        using var start = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start", "{}");
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        using var queue = await Send(HttpMethod.Get, "/api/v1/pr-reviews/queue");
        Assert.Equal(HttpStatusCode.Conflict, queue.StatusCode);
    }

    [Fact]
    public async Task A_review_round_trips_through_the_api()
    {
        using var started = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start",
            """{"title":"feat: widgets","author":"octocat","openedAt":"2026-08-01T14:00:00Z","headSha":"abc123"}""");
        using var resumed = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start");
        using var completed = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/complete",
            """{"feedback":"## Lead review","verdict":"request_changes"}""");
        using var detail = await Send(HttpMethod.Get, "/api/v1/pr-reviews/7");
        using var list = await Send(HttpMethod.Get, "/api/v1/pr-reviews");

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var startedJson = await Json(started);
        Assert.True(startedJson.GetProperty("created").GetBoolean());
        Assert.Equal("in_progress", startedJson.GetProperty("review").GetProperty("status").GetString());
        var detailJson = await Json(detail);
        Assert.Equal("blocker", detailJson.GetProperty("pullRequest").GetProperty("status").GetString());
        Assert.Equal("https://github.com/octo/widgets/pull/7", detailJson.GetProperty("url").GetString());
        Assert.True(detailJson.GetProperty("reviews")[0].GetProperty("blocking").GetBoolean());
        Assert.Equal("failed", detailJson.GetProperty("deliveries")[0].GetProperty("status").GetString());
        Assert.Equal("REQUEST_CHANGES", detailJson.GetProperty("deliveries")[0].GetProperty("event").GetString());
        Assert.Equal(JsonValueKind.Null, detailJson.GetProperty("deliveries")[0].GetProperty("gitHubId").ValueKind);
        var listJson = await Json(list);
        Assert.Equal("octo/widgets", listJson.GetProperty("repository").GetString());
        Assert.Equal(7, listJson.GetProperty("pullRequests")[0].GetProperty("number").GetInt32());
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());

        var reviewId = detailJson.GetProperty("reviews")[0].GetProperty("id").GetInt64();
        using var cleared = await Send(HttpMethod.Put, $"/api/v1/pr-reviews/reviews/{reviewId}", """{"blocking":false}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False((await Json(cleared)).GetProperty("blocking").GetBoolean());
        // Deletes answer 200 with a body, because the dashboard's apiCall always parses JSON.
        using var deleted = await Send(HttpMethod.Delete, $"/api/v1/pr-reviews/reviews/{reviewId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedJson = await Json(deleted);
        Assert.Equal(7, deletedJson.GetProperty("number").GetInt32());
        Assert.Equal(reviewId, deletedJson.GetProperty("reviewId").GetInt64());
        using var deletedPr = await Send(HttpMethod.Delete, "/api/v1/pr-reviews/7");
        Assert.Equal(HttpStatusCode.OK, deletedPr.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(deletedPr)).GetProperty("reviewId").ValueKind);
    }

    [Fact]
    public async Task Errors_carry_their_status_and_a_readable_message()
    {
        using var managed = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start", """{"startedAt":"2026-01-01"}""");
        using var missing = await Send(HttpMethod.Get, "/api/v1/pr-reviews/999");
        using var untracked = await Send(HttpMethod.Post, "/api/v1/pr-reviews/999/reviews/draft", """{"feedback":"x"}""");
        await Send(HttpMethod.Post, "/api/v1/pr-reviews",
            """{"number":8,"title":"t","author":"a","openedAt":"2026-08-01T00:00:00Z"}""");
        using var notStarted = await Send(HttpMethod.Post, "/api/v1/pr-reviews/8/reviews/complete",
            """{"feedback":"x","verdict":"comments"}""");

        Assert.Equal(HttpStatusCode.BadRequest, managed.StatusCode);
        Assert.Equal("startedAt is managed by the server", (await Json(managed)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, untracked.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notStarted.StatusCode);
        Assert.Contains("does not have an in-progress review", (await Json(notStarted)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Divergent_legacy_history_returns_a_safe_409_message()
    {
        _repositories.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrReviewAdoptionConflictException(7));
        using var response = await Send(HttpMethod.Get, "/api/v1/pr-reviews");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(new PrReviewAdoptionConflictException(7).Message,
            (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Conflicts_are_inspectable_and_resolved_with_version_tokens_through_the_API()
    {
        var legacy = new PrReviewStore($"Data Source={_dbPath}.legacy;Mode=ReadWriteCreate");
        await legacy.StartReviewAsync("octo/widgets", 7, new("title", "author", DateTime.UtcNow), null, Ct);
        using var adopted = await Send(HttpMethod.Get, "/api/v1/pr-reviews");
        using var edited = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/draft", """{"feedback":"local"}""");
        await legacy.SaveDraftAsync("octo/widgets", 7, new(Feedback: "legacy draft"), null, Ct);
        using var list = await Send(HttpMethod.Get, "/api/v1/pr-reviews");
        var conflict = (await Json(list)).GetProperty("legacyConflicts")[0];
        Assert.Equal("legacy draft", conflict.GetProperty("legacy").GetProperty("reviews")[0].GetProperty("feedback").GetString());
        using var stale = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/legacy-conflict/resolve",
            JsonSerializer.Serialize(new { sourceHash = new string('0', 64), targetHash = new string('0', 64), useLegacy = true }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var resolved = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/legacy-conflict/resolve",
            JsonSerializer.Serialize(new { sourceHash = conflict.GetProperty("sourceHash").GetString(),
                targetHash = conflict.GetProperty("targetHash").GetString(), useLegacy = true }));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        using var detail = await Send(HttpMethod.Get, "/api/v1/pr-reviews/7");
        Assert.Equal("legacy draft", (await Json(detail)).GetProperty("reviews")[0].GetProperty("feedback").GetString());
    }

    [Fact]
    public async Task The_queue_answers_502_when_github_cannot_be_reached()
    {
        _github.Setup(g => g.ListOpenAsync(Repo, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubCliException("Unable to list open pull requests for octo/widgets: offline"));

        using var queue = await Send(HttpMethod.Get, "/api/v1/pr-reviews/queue");

        Assert.Equal(HttpStatusCode.BadGateway, queue.StatusCode);
        Assert.Contains("offline", (await Json(queue)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_detail_page_can_start_a_pass_at_the_current_github_head()
    {
        _github.Setup(g => g.GetAsync(Repo, It.IsAny<string>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest(7, "Live title", "octocat", new DateTime(2026, 8, 1), null,
                false, "abc1234", Repo.PullRequestUrl(7)));

        using var started = await Send(HttpMethod.Post, "/api/v1/pr-reviews/7/reviews/start", """{"useCurrentHead":true}""");

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var json = await Json(started);
        Assert.Equal("abc1234", json.GetProperty("review").GetProperty("headSha").GetString());
        Assert.Equal("Live title", json.GetProperty("pullRequest").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Sync_reports_what_github_said()
    {
        await Send(HttpMethod.Post, "/api/v1/pr-reviews",
            """{"number":7,"title":"t","author":"a","openedAt":"2026-08-01T00:00:00Z"}""");
        _github.Setup(g => g.GetStatesAsync(Repo, It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PrGitHubStateUpdate(7, PrGitHubState.Closed, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc))]);

        using var sync = await Send(HttpMethod.Post, "/api/v1/pr-reviews/github/sync");

        var json = await Json(sync);
        Assert.Equal(1, json.GetProperty("tracked").GetInt32());
        Assert.Equal(1, json.GetProperty("closed").GetInt32());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null,
        bool session = true, bool tab = true)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (session) request.Headers.Add("viberails_session", "test-session");
        if (tab) request.Headers.Add("viberails_tab", "test-tab");
        return await SharedClient.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }
}
