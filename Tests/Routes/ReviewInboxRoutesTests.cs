using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Services.ReviewRequests;
using VibeRails.DB;
using VibeRails.Routes;
using VibeRails.Services.ReviewRequests;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The inbox's clone choice (VIBE-213 item 7) through the real route: the list carries the open roots'
/// clones, and approve's optional body names one of them. Credentials are covered by AgentCredentialTests
/// and the middleware suites, so this host maps the routes alone. One client per class.
/// </summary>
[Collection("ProcessEnvIsolation")] // mutates ParserConfigs.SetGitState (process-global)
public sealed class ReviewInboxRoutesTests : IAsyncLifetime
{
    private static readonly HttpClient SharedClient = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-review-inbox-routes-{Guid.NewGuid():N}");
    private readonly string _originalRootPath = ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = ParserConfigs.GetIsInGit();
    private readonly RecordingLauncher _launcher = new();
    private string _project = null!;
    private string _other = null!;
    private WebApplication _app = null!;
    private Uri _baseUri = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _project = Path.Combine(_root, "vibe-rails");
        _other = Path.Combine(_root, "review-clone");
        ParserConfigs.SetGitState(_project, isInGit: true);
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(j => j.GetOpenProjectRootsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([_project, _other]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(jobs.Object);
        builder.Services.AddSingleton(new ReviewInboxService(
            () => ReviewRequestTestSupport.Transport(_ => throw new InvalidOperationException("No hosted call is expected.")),
            new ReviewInboxState(), () => "key-1", () => "me@example.test", new ReviewRequestTestSupport.RecordingEventBus(), _launcher));
        _app = builder.Build();
        ReviewInboxRoutes.Map(_app);
        await _app.StartAsync(Ct);
        _baseUri = new Uri(_app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        ParserConfigs.SetGitState(_originalRootPath, _originalIsInGit);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheListCarriesThisProjectThenTheOtherOpenClones()
    {
        using var response = await SharedClient.GetAsync(new Uri(_baseUri, "/api/v1/review-inbox"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var clones = body.RootElement.GetProperty("clones").EnumerateArray()
            .Select(clone => (clone.GetProperty("path").GetString(), clone.GetProperty("name").GetString(), clone.GetProperty("current").GetBoolean()))
            .ToList();
        Assert.Equal([(_project, "vibe-rails", true), (_other, "review-clone", false)], clones);
    }

    [Fact]
    public async Task ApproveRebuildsInTheNamedOpenCloneOrThisProject()
    {
        using var chosen = await Approve(Reference('1'), JsonSerializer.Serialize(new { repositoryPath = _other }));
        Assert.Equal(HttpStatusCode.Accepted, chosen.StatusCode);
        using var none = await Approve(Reference('2'), "{}");
        Assert.Equal(HttpStatusCode.Accepted, none.StatusCode);
        using var empty = await Approve(Reference('3'), null);
        Assert.Equal(HttpStatusCode.Accepted, empty.StatusCode);
        Assert.Equal([(Reference('1'), _other), (Reference('2'), _project), (Reference('3'), _project)], _launcher.Launched);
    }

    [Fact]
    public async Task ApproveRefusesAFolderNoRootHasAndBadBodies()
    {
        using var elsewhere = await Approve(Reference('4'), JsonSerializer.Serialize(new { repositoryPath = Path.Combine(_root, "elsewhere") }));
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Contains("\"invalid_request\"", await elsewhere.Content.ReadAsStringAsync(Ct));
        using var malformed = await Approve(Reference('5'), "{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var large = await Approve(Reference('6'), JsonSerializer.Serialize(new { repositoryPath = new string('x', 20_000) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Empty(_launcher.Launched);
    }

    private static string Reference(char fill) => "rvw_" + new string(fill, 32);

    private Task<HttpResponseMessage> Approve(string reference, string? json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"/api/v1/review-inbox/{reference}/approve"));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return SharedClient.SendAsync(request, Ct);
    }

    /// <summary>Records the clone each popup was opened for; every popup closes at once without approving.</summary>
    private sealed class RecordingLauncher : IReviewSignaturePopupLauncher
    {
        public bool Available => true;
        public List<(string Reference, string Repository)> Launched { get; } = [];

        public Task<int?> LaunchAsync(string reference, string repositoryPath, CancellationToken ct)
        {
            lock (Launched) Launched.Add((reference, repositoryPath));
            return Task.FromResult<int?>(1);
        }
    }
}
