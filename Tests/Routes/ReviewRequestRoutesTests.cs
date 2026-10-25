using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Services.ReviewRequests;
using Tests.VcaRegression;
using VibeRails.Auth;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.Terminal;
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
    private static readonly string ApprovedCommit = new('e', 40);
    /// <summary>The session a CLI's tool contact is bound to (request_review, slice E).</summary>
    private static readonly string ToolSession = Guid.NewGuid().ToString("D");
    private readonly AgentCredentialService _credentials = new();
    private AgentCredentialService.Contact _contact = null!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-review-routes-{Guid.NewGuid():N}");
    private readonly string _originalRootPath = ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = ParserConfigs.GetIsInGit();
    private readonly FakeCommitSignatureVerifier _verifier = FakeCommitSignatureVerifier.ValidFor("lead@example.com");
    private readonly Mock<ISessionArchiveReader> _archives = new();
    private readonly ReviewRequestTestSupport.RecordingEventBus _events = new();
    private readonly List<HttpRequestMessage> _outbound = [];
    private HttpStatusCode _hostedDelete = HttpStatusCode.OK;
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
        var started = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        sessions.Setup(s => s.GetSessionByIdAsync(ToolSession, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(ToolSession, "codex", null, _project, started, null, null));
        sessions.Setup(s => s.GetSessionDisplayInfoAsync(ToolSession)).ReturnsAsync(("codex", "Tool session"));
        // The contact a VibeRails-launched CLI inherits as VIBERAILS_TOOL_*: bound to its live session,
        // which the middleware confirms through the terminal session service (the native path).
        _contact = _credentials.ForTools(ToolSession);
        _credentials.BindSession(ToolSession, ToolSession);
        var terminal = new Mock<ITerminalSessionService>();
        terminal.SetupGet(t => t.HasActiveSession).Returns(true);
        terminal.SetupGet(t => t.ActiveSessionId).Returns(ToolSession);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(sessions.Object);
        builder.Services.AddSingleton(_credentials);
        builder.Services.AddSingleton(terminal.Object);
        var store = new ReviewRequestStore(connection);
        // Slice E: the commit-approvals route resolves commit trees through this fake instead of git.
        var reviews = new ReviewRequestService(store, snapshots.Object, _verifier, commitTrees: (_, shas, _) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(shas
                .Where(sha => ApprovedCommit.StartsWith(sha, StringComparison.Ordinal))
                .ToDictionary(sha => sha, _ => Tree, StringComparer.Ordinal)));
        builder.Services.AddSingleton(reviews);
        // Slice C: the sender behind disclosure/send/cancel, with viberails.ai intercepted.
        var transport = ReviewRequestTestSupport.Transport(request =>
        {
            _outbound.Add(request);
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(_hostedDelete));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStream());
            var requestId = body.RootElement.GetProperty("requestId").GetString()!;
            return Task.FromResult(ReviewRequestTestSupport.Json(HttpStatusCode.Created, ReviewRequestTestSupport.Hosted(requestId, Tree)));
        });
        builder.Services.AddSingleton(new ReviewRequestSender(store, reviews, transport, _archives.Object, _events, new ReviewRequestPollState(), () => "routes-fixture-key"));
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
            (HttpMethod.Post, $"/api/v1/review-requests/{id}/cancel"), (HttpMethod.Delete, $"/api/v1/review-requests/{id}"),
            (HttpMethod.Get, $"/api/v1/review-requests/{id}/disclosure"), (HttpMethod.Post, $"/api/v1/review-requests/{id}/send"),
            (HttpMethod.Get, "/api/v1/review-requests/staged"), (HttpMethod.Get, $"/api/v1/review-requests/commit-approvals?sha={ApprovedCommit}")];
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

        // Slice F: a rejection with line comments is stored and answered with them, grouped as sent.
        using var rejected = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "rejected", reviewer = "lead@example.com", comment = "one more case",
                lineComments = new[] { new { path = "db.cs", line = 1, side = "after", text = "Rename this." } } }));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        using var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(Ct));
        Assert.Equal("rejected", rejectedJson.RootElement.GetProperty("status").GetString());
        var lineComment = Assert.Single(Assert.Single(rejectedJson.RootElement.GetProperty("decisions").EnumerateArray()).GetProperty("lineComments").EnumerateArray());
        Assert.Equal(("db.cs", 1, "after", "Rename this."), (lineComment.GetProperty("path").GetString(), lineComment.GetProperty("line").GetInt32(),
            lineComment.GetProperty("side").GetString(), lineComment.GetProperty("text").GetString()));
        using var tooManyLines = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "rejected", reviewer = "lead@example.com",
                lineComments = Enumerable.Range(1, ReviewRequestService.MaxLineComments + 1).Select(i => new { path = "db.cs", line = i, side = "after", text = "x" }).ToArray() }));
        Assert.Equal(HttpStatusCode.BadRequest, tooManyLines.StatusCode);
        Assert.Equal("invalid_request", await CodeAsync(tooManyLines));

        using var approved = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "approved", trailer = HumanSignatureRuleTests.SignatureLine(Tree), comment = "fine" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(Ct));
        Assert.Equal("approved", approvedJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, approvedJson.RootElement.GetProperty("decisions").GetArrayLength());
        var decision = approvedJson.RootElement.GetProperty("decisions").EnumerateArray().Single(d => d.GetProperty("decision").GetString() == "approved");
        Assert.False(decision.TryGetProperty("lineComments", out _));
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

    /// <summary>
    /// Slice E (Codex review F1): a CLI's tool contact reaches exactly create and disclosure through
    /// the real middleware, may attach only its own session, reads only a request that attaches it,
    /// and can never list, preview, send, decide, cancel or delete.
    /// </summary>
    [Fact]
    public async Task AToolContactCreatesWithItsOwnSessionOnlyAndReadsOnlyItsOwnDisclosure()
    {
        using var created = await SendAs(_contact, HttpMethod.Post, "/api/v1/review-requests",
            $"{{\"reviewers\":[\"lead@example.com\"],\"sessions\":[\"{ToolSession}\"],\"cardKey\":\"VB-50\",\"note\":\"from the tool\"}}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdJson.RootElement.GetProperty("id").GetString();
        Assert.Equal(1, createdJson.RootElement.GetProperty("sessionCount").GetInt32());

        // No sessions named: the contact's own session is attached anyway. Another session: refused.
        using var implicitSession = await SendAs(_contact, HttpMethod.Post, "/api/v1/review-requests", "{\"reviewers\":[\"lead@example.com\"]}");
        Assert.Equal(HttpStatusCode.Created, implicitSession.StatusCode);
        var implicitId = JsonDocument.Parse(await implicitSession.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString();
        using var implicitDetail = await Send(HttpMethod.Get, $"/api/v1/review-requests/{implicitId}");
        Assert.Equal(ToolSession, Assert.Single(JsonDocument.Parse(await implicitDetail.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("bundle").GetProperty("sessions").EnumerateArray()).GetProperty("id").GetString());
        using var foreign = await SendAs(_contact, HttpMethod.Post, "/api/v1/review-requests",
            $"{{\"reviewers\":[\"lead@example.com\"],\"sessions\":[\"{ToolSession}\",\"{Guid.NewGuid():D}\"]}}");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("forbidden", await CodeAsync(foreign));

        using var disclosure = await SendAs(_contact, HttpMethod.Get, $"/api/v1/review-requests/{id}/disclosure");
        Assert.Equal(HttpStatusCode.OK, disclosure.StatusCode);
        using var disclosureJson = JsonDocument.Parse(await disclosure.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ToolSession, Assert.Single(disclosureJson.RootElement.GetProperty("sessions").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("from the tool", disclosureJson.RootElement.GetProperty("note").GetString());

        // A request the dashboard made without this session is not the contact's to read.
        using var dashboard = await Send(HttpMethod.Post, "/api/v1/review-requests", "{\"reviewers\":[\"lead@example.com\"]}");
        var otherId = JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString();
        using var notOwned = await SendAs(_contact, HttpMethod.Get, $"/api/v1/review-requests/{otherId}/disclosure");
        Assert.Equal(HttpStatusCode.Forbidden, notOwned.StatusCode);
        Assert.Equal("forbidden", await CodeAsync(notOwned));
        using var unknown = await SendAs(_contact, HttpMethod.Get, $"/api/v1/review-requests/{Guid.NewGuid():D}/disclosure");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Everything that transfers, decides or removes stays with the dashboard credentials.
        (HttpMethod Method, string Path)[] denied = [
            (HttpMethod.Get, "/api/v1/review-requests"), (HttpMethod.Get, "/api/v1/review-requests/staged"),
            (HttpMethod.Get, $"/api/v1/review-requests/commit-approvals?sha={ApprovedCommit}"), (HttpMethod.Get, $"/api/v1/review-requests/{id}"),
            (HttpMethod.Post, $"/api/v1/review-requests/{id}/send"), (HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions"),
            (HttpMethod.Post, $"/api/v1/review-requests/{id}/cancel"), (HttpMethod.Delete, $"/api/v1/review-requests/{id}")];
        foreach (var (method, path) in denied)
        {
            using var refused = await SendAs(_contact, method, path, "{\"confirmed\":true}");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        Assert.Empty(_outbound);
        foreach (var requestId in new[] { id, implicitId, otherId })
        {
            using var deleted = await Send(HttpMethod.Delete, $"/api/v1/review-requests/{requestId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    /// <summary>Slice E: the card's preview reads the staged index without storing anything, and a linked commit's approval is read from the ledger.</summary>
    [Fact]
    public async Task StagedPreviewStoresNothingAndCommitApprovalsReadTheLedger()
    {
        using var preview = await Send(HttpMethod.Get, "/api/v1/review-requests/staged");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.True(preview.Headers.CacheControl?.NoStore);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Tree, previewJson.RootElement.GetProperty("treeId").GetString());
        var file = Assert.Single(previewJson.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("db.cs", file.GetProperty("path").GetString());
        Assert.Equal(1, file.GetProperty("added").GetInt32());
        Assert.Equal(1, file.GetProperty("removed").GetInt32());
        Assert.True(file.GetProperty("textIncluded").GetBoolean());
        Assert.Equal("email", previewJson.RootElement.GetProperty("audience").GetString());
        Assert.Empty(previewJson.RootElement.GetProperty("suggestedReviewers").EnumerateArray());
        using var list = await Send(HttpMethod.Get, "/api/v1/review-requests");
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        Assert.Empty(listJson.RootElement.GetProperty("requests").EnumerateArray());

        using var none = await Send(HttpMethod.Get, $"/api/v1/review-requests/commit-approvals?sha={ApprovedCommit}");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        using var noneJson = JsonDocument.Parse(await none.Content.ReadAsStringAsync(Ct));
        var pending = Assert.Single(noneJson.RootElement.GetProperty("commits").EnumerateArray());
        Assert.Equal(Tree, pending.GetProperty("treeId").GetString());
        Assert.Empty(pending.GetProperty("approvals").EnumerateArray());

        using var created = await Send(HttpMethod.Post, "/api/v1/review-requests", "{\"reviewers\":[\"lead@example.com\"],\"cardKey\":\"VB-50\"}");
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString();
        using var approved = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/decisions",
            JsonSerializer.Serialize(new { decision = "approved", trailer = HumanSignatureRuleTests.SignatureLine(Tree) }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var found = await Send(HttpMethod.Get, $"/api/v1/review-requests/commit-approvals?sha={ApprovedCommit[..12]},{new string('0', 40)}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using var foundJson = JsonDocument.Parse(await found.Content.ReadAsStringAsync(Ct));
        var commits = foundJson.RootElement.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal(2, commits.Count);
        var approval = Assert.Single(commits[0].GetProperty("approvals").EnumerateArray());
        Assert.Equal("lead@example.com", approval.GetProperty("reviewer").GetString());
        Assert.Equal(id, approval.GetProperty("requestId").GetString());
        // Null properties are omitted by the context's WhenWritingNull option.
        Assert.False(commits[1].TryGetProperty("treeId", out var missingTree) && missingTree.ValueKind != JsonValueKind.Null);
        Assert.Empty(commits[1].GetProperty("approvals").EnumerateArray());

        using var invalid = await Send(HttpMethod.Get, "/api/v1/review-requests/commit-approvals?sha=not-hex");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", await CodeAsync(invalid));
        using var deleted = await Send(HttpMethod.Delete, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    /// <summary>Slice C: nothing leaves without the confirmed send; the send records the hosted copy; cancel reaches the host.</summary>
    [Fact]
    public async Task DisclosureThenConfirmedSendThenCancelRoundTrip()
    {
        using var created = await Send(HttpMethod.Post, "/api/v1/review-requests", "{\"reviewers\":[\"lead@example.com\"],\"cardKey\":\"VB-50\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.False(createdJson.RootElement.TryGetProperty("remote", out _));

        using var disclosure = await Send(HttpMethod.Get, $"/api/v1/review-requests/{id}/disclosure");
        Assert.Equal(HttpStatusCode.OK, disclosure.StatusCode);
        Assert.True(disclosure.Headers.CacheControl?.NoStore);
        using var disclosureJson = JsonDocument.Parse(await disclosure.Content.ReadAsStringAsync(Ct));
        Assert.Equal("https://viberails.ai", disclosureJson.RootElement.GetProperty("destination").GetString());
        Assert.Equal("email", disclosureJson.RootElement.GetProperty("audience").GetString());
        Assert.Equal("db.cs", Assert.Single(disclosureJson.RootElement.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());
        Assert.Equal("lead@example.com", Assert.Single(disclosureJson.RootElement.GetProperty("reviewers").EnumerateArray()).GetString());
        Assert.False(disclosureJson.RootElement.GetProperty("sent").GetBoolean());
        Assert.Empty(_outbound);

        using var unconfirmed = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/send", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("confirmation_required", await CodeAsync(unconfirmed));
        using var oversized = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/send", "{\"confirmed\":true,\"pad\":\"" + new string('x', ReviewRequestRoutes.MaxSendBodyBytes) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Empty(_outbound);

        using var sent = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/send", "{\"confirmed\":true}");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var sentJson = JsonDocument.Parse(await sent.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ReviewRequestTestSupport.Reference, sentJson.RootElement.GetProperty("request").GetProperty("remote").GetProperty("id").GetString());
        Assert.Contains("Decisions are checked every 15 minutes", sentJson.RootElement.GetProperty("message").GetString());
        var post = Assert.Single(_outbound);
        Assert.Equal((HttpMethod.Post, "routes-fixture-key"), (post.Method, Assert.Single(post.Headers.GetValues("X-Api-Key"))));
        Assert.Equal("review_request_updated", Assert.Single(_events.Events).Type);

        // The host is down: the local cancel succeeds, the hosted cancellation stays pending, and the
        // row cannot be deleted yet because it is the only thing that can still cancel the hosted copy (review F1).
        _hostedDelete = HttpStatusCode.ServiceUnavailable;
        using var cancelled = await Send(HttpMethod.Post, $"/api/v1/review-requests/{id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var cancelledJson = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync(Ct));
        Assert.Equal("cancelled", cancelledJson.RootElement.GetProperty("status").GetString());
        Assert.False(cancelledJson.RootElement.GetProperty("remote").TryGetProperty("cancelledUtc", out _));
        Assert.Equal(HttpMethod.Delete, _outbound[^1].Method);
        Assert.EndsWith("/api/v1/review-requests/" + ReviewRequestTestSupport.Reference, _outbound[^1].RequestUri!.AbsolutePath);
        using var kept = await Send(HttpMethod.Delete, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.Conflict, kept.StatusCode);
        Assert.Equal("remote_pending", await CodeAsync(kept));
        using var still = await Send(HttpMethod.Get, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        // The host answers again: the hosted copy is cancelled and the row goes.
        _hostedDelete = HttpStatusCode.OK;
        using var deleted = await Send(HttpMethod.Delete, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpMethod.Delete, _outbound[^1].Method);
        using var gone = await Send(HttpMethod.Get, $"/api/v1/review-requests/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("deleted", _events.Events[^1].Payload.GetProperty("status").GetString());
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("code").GetString();
    }

    /// <summary>A request with a CLI's scoped tool contact instead of the dashboard pair.</summary>
    private async Task<HttpResponseMessage> SendAs(AgentCredentialService.Contact contact, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.Add("viberails_session", contact.Session);
        request.Headers.Add("viberails_tab", contact.Tab);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SharedClient.SendAsync(request, Ct);
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
