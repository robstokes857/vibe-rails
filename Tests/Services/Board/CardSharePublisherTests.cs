using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sharing;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Board;

[Collection("ProcessEnvIsolation")]
public sealed class CardSharePublisherTests : IDisposable
{
    private const string Key = "card-share-fixture-key";
    private readonly string _originalKey = ParserConfigs.GetApiKey();
    private readonly string _originalProject = ParserConfigs.GetRootPath();
    private readonly bool _originalGit = ParserConfigs.GetIsInGit();
    private readonly string _lockPath = Path.Combine(Path.GetTempPath(), "card-share-" + Guid.NewGuid().ToString("N") + ".lock");
    private readonly Mock<IBoardStore> _boards = new();
    private readonly Mock<ISessionArchiveReader> _archives = new(MockBehavior.Strict);
    private readonly Mock<ISessionStore> _sessions = new();
    private readonly Mock<IChatSummaryStore> _summaries = new();
    private readonly Guid _session = Guid.NewGuid();
    private readonly CardShareRefreshState _state = new();
    private readonly BoardCardRecord _card;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public CardSharePublisherTests()
    {
        ParserConfigs.SetApiKey(Key);
        ParserConfigs.SetGitState("project", true);
        var now = DateTime.UtcNow;
        _card = new("card_test", "project", 123, "lane", 0, "Complete card", "Description", null, "medium", 3, [], false, 1,
            now, now, BoardId: "board", StoredKey: "VB-CARDS-123");
        _boards.Setup(b => b.FindCardAsync("project", "card_test", It.IsAny<CancellationToken>())).ReturnsAsync(_card);
        _boards.Setup(b => b.FindLocalCardAsync(_card.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_card);
        var recordings = new[] { new BoardSessionRecord(_session.ToString("D"), "card_test", null, "base:codex", "codex", "Implementation", "launch", now) };
        _boards.Setup(b => b.GetCardDetailAsync("project", "card_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCardDetailRecord(_card, [], recordings, [], [], []));
        _boards.Setup(b => b.GetSyncActivityAsync("project", "board", "card_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardSyncActivityRecord(recordings, [], [], []));
        _boards.Setup(b => b.GetCardHistoryAsync("project", "card_test", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _boards.Setup(b => b.GetReviewsAsync("project", "card_test", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _boards.Setup(b => b.GetChecksAsync("project", "card_test", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _summaries.Setup(s => s.GetChatSummariesBySessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(_session.ToString("D"), SessionSharingService.KeyFingerprint(Key), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }
    private CardShareCapture Capture() => new(_boards.Object, _sessions.Object, _summaries.Object);
    private CardSharePublisher Publisher(HttpClient http) => new(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), _state);
    private CardShareLinkDto Link() => new(7, _card.Key, "Demo", "/shared/card#key=" + new string('a', 64), DateTime.UtcNow,
        DateTime.UtcNow.AddMonths(1), "active", DateTime.UtcNow, _card.Id);
    private HttpResponseMessage Published(CardSharePublished? result = null) => new(HttpStatusCode.Created)
    { Content = JsonContent.Create(result ?? new CardSharePublished(Link(), [_session]), CardSharingJsonContext.Default.CardSharePublished) };

    [Fact]
    public async Task CapturesCompleteSavedCard_UsesPinnedOrigin_AndQueuesWithOriginalAccountAfterSettingsChange()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://viberails.ai/api/v1/card-sharing-links", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.False(request.Headers.Contains("Cookie"));
            var sent = await request.Content!.ReadFromJsonAsync(CardSharingJsonContext.Default.CardSharePublishRequest, Ct);
            Assert.Equal("Demo", sent!.DisplayName); Assert.Equal(_card.Id, sent.LocalCardId); Assert.Equal(_session, Assert.Single(sent.Snapshot.Sessions).SourceId);
            ParserConfigs.SetApiKey("different-fixture-key");
            return Published();
        }));
        var result = await Publisher(http).CreateAsync("project", "card_test", " Demo ", Ct);
        Assert.True(result.Success); Assert.Contains("previous account", result.Message);
        _archives.Verify(a => a.EnsureSessionShareUploadAsync(_session.ToString("D"), SessionSharingService.KeyFingerprint(Key), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        using var handler = Assert.IsType<HttpClientHandler>(CardShareClient.CreateHandler());
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies);
    }

    [Theory]
    [InlineData("foreign_session")]
    [InlineData("foreign_card")]
    [InlineData("foreign_local_id")]
    [InlineData("external_url")]
    [InlineData("expiry")]
    [InlineData("missing_sessions")]
    [InlineData("duplicate_session")]
    [InlineData("redirect")]
    [InlineData("oversized")]
    [InlineData("invalid_json")]
    public async Task RejectsUntrustedAcknowledgementsBeforeDurableUpload(string variant)
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var link = Link();
            if (variant == "foreign_local_id") link = link with { LocalCardId = "card_other" };
            if (variant == "foreign_card") link = link with { SourceKey = "VB-OTHER-1" };
            if (variant == "external_url") link = link with { SharePath = "https://example.invalid/" + Key };
            if (variant == "expiry") link = link with { ExpiresUtc = DateTime.UtcNow.AddYears(1) };
            var result = new CardSharePublished(link, variant == "foreign_session" ? [Guid.NewGuid()]
                : variant == "missing_sessions" ? null! : variant == "duplicate_session" ? [_session, _session] : [_session]);
            return Task.FromResult(variant switch
            {
                "redirect" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.invalid/" + Key) } },
                "oversized" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) },
                "invalid_json" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{bad:" + Key) },
                _ => Published(result)
            });
        }));
        var result = await Publisher(http).CreateAsync("project", "card_test", "Demo", Ct);
        Assert.False(result.Success); Assert.DoesNotContain(Key, result.Message); Assert.Contains("link", result.Message);
        _archives.Verify(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScopedCrudRejectsForeignCardOrLink_AndNeverRepeatsUncertainCreation()
    {
        var posts = 0; var mutations = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException(Key); }
            if (request.Method != HttpMethod.Get) mutations++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new List<CardShareLinkDto> { Link() }, CardSharingJsonContext.Default.ListCardShareLinkDto) });
        }));
        var publisher = Publisher(http);
        Assert.False((await publisher.CreateAsync("foreign", "card_test", "Demo", Ct)).Success);
        Assert.Equal(0, posts);
        var failed = await publisher.CreateAsync("project", "card_test", "Demo", Ct);
        Assert.False(failed.Success); Assert.Contains("may already", failed.Message); Assert.Equal(1, posts);
        Assert.False((await publisher.RenameAsync("project", "card_test", 8, "Wrong link", Ct)).Success);
        Assert.False((await publisher.RevokeAsync("foreign", "card_test", 7, Ct)).Success);
        Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundRefresh_OnlyFindsStoredLocalSources_DoesNotResendUnchangedContent_AndRecoversAfterRestart(bool recordingAvailable)
    {
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(_session.ToString("D"), SessionSharingService.KeyFingerprint(Key), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(recordingAvailable);
        var snapshot = await Capture().CaptureAsync("project", "card_test", Ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, CardSharingJsonContext.Default.CardShareSnapshot)));
        var updates = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(Key, request.Headers.GetValues("X-Api-Key").Single());
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new List<CardShareSource> { new(1, "VB-OTHER-1", hash), new(2, _card.Key, hash, _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) };
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith("/sources/2", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadFromJsonAsync(CardSharingJsonContext.Default.CardShareRefreshRequest, Ct);
            Assert.Equal(_card.Key, body!.Snapshot.SourceKey); Assert.Equal(hash, body.Revision);
            updates++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new CardShareRefreshResult(true, [_session]), CardSharingJsonContext.Default.CardShareRefreshResult) };
        }));
        await Publisher(http).RefreshDueAsync(Ct);
        await Publisher(http).RefreshDueAsync(Ct);
        Assert.Equal(1, updates);
        var restarted = new CardSharePublisher(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), new());
        await restarted.RefreshDueAsync(Ct);
        Assert.Equal(2, updates);
        _archives.Verify(a => a.EnsureSessionShareUploadAsync(_session.ToString("D"), SessionSharingService.KeyFingerprint(Key), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _boards.Verify(b => b.GetCardDetailAsync(It.IsAny<string>(), "VB-OTHER-1", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AccountChangeDuringDiscoveryPreventsAnyBackgroundPublication()
    {
        var updates = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) updates++;
            ParserConfigs.SetApiKey("changed-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new List<CardShareSource> { new(1, _card.Key, new string('a', 64), _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) });
        }));
        await Assert.ThrowsAsync<BoardValidationException>(() => Publisher(http).RefreshDueAsync(Ct));
        Assert.Equal(0, updates);
        _boards.Verify(b => b.FindLocalCardAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LocalRoutesRequireBothCredentials_RespectProject_BoundChunkedBodies_AndKeepRemote401AsDomainOutcome()
    {
        var calls = 0;
        using var remote = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        var auth = new AuthService();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAuthService>(auth); builder.Services.AddSingleton(Publisher(remote));
        await using var app = builder.Build(); app.UseMiddleware<CookieAuthMiddleware>(); BoardRoutes.MapCardSharing(app);
        await app.StartAsync(Ct);
        try
        {
            using var local = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string path = "/api/v1/board/cards/card_test/sharing-links";
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete })
            foreach (var (session, tab) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                using var request = new HttpRequestMessage(method, path + (method == HttpMethod.Patch || method == HttpMethod.Delete ? "/7" : ""));
                if (method == HttpMethod.Post || method == HttpMethod.Patch) request.Content = JsonContent.Create(new CardShareNameRequest("Demo"), CardSharingJsonContext.Default.CardShareNameRequest);
                if (session) request.Headers.Add("viberails_session", auth.GetInstanceToken());
                if (tab) request.Headers.Add("viberails_tab", auth.GetTabToken());
                using var response = await local.SendAsync(request, Ct);
                Assert.Equal(session && tab ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
                if (session && tab) { Assert.True(response.Headers.CacheControl!.NoStore); Assert.False((await response.Content.ReadFromJsonAsync(CardSharingJsonContext.Default.CardShareResult, Ct))!.Success); }
            }
            Assert.Equal(4, calls);
            local.DefaultRequestHeaders.Add("viberails_session", auth.GetInstanceToken()); local.DefaultRequestHeaders.Add("viberails_tab", auth.GetTabToken());
            using var foreign = await local.GetAsync(path.Replace("card_test", "foreign"), Ct);
            Assert.False((await foreign.Content.ReadFromJsonAsync(CardSharingJsonContext.Default.CardShareResult, Ct))!.Success);
            foreach (var chunked in new[] { false, true })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(new string('x', 4100), Encoding.UTF8, "application/json") };
                request.Headers.TransferEncodingChunked = chunked;
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await local.SendAsync(request, Ct)).StatusCode);
            }
            Assert.Equal(4, calls);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request); }
    public void Dispose()
    {
        ParserConfigs.SetApiKey(_originalKey); ParserConfigs.SetGitState(_originalProject, _originalGit);
        if (File.Exists(_lockPath)) File.Delete(_lockPath);
    }
}
