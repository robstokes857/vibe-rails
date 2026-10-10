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
        var hash = (await Capture().CaptureAsync("project", "card_test", Ct)).Hash;
        var updates = 0; var discoveries = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(Key, request.Headers.GetValues("X-Api-Key").Single());
            if (request.Method == HttpMethod.Get) { discoveries++; return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new List<CardShareSource> { new(1, "VB-OTHER-1", hash), new(2, _card.Key, hash, _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) }; }
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
        Assert.Equal(1, discoveries); // The second tick within the interval makes no request at all.
        // Memory-only state: a restart reconciles each publication once. The persisted state test covers the file.
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

    [Fact]
    public async Task CreateConfirmsThePublishedSnapshot_SoTheNextSweepTransfersNothing()
    {
        var hash = (await Capture().CaptureAsync("project", "card_test", Ct)).Hash;
        var puts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(Published());
            if (request.Method == HttpMethod.Put) puts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new List<CardShareSource> { new(2, _card.Key, hash, _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) });
        }));
        var publisher = Publisher(http);
        Assert.True((await publisher.CreateAsync("project", "card_test", "Demo", Ct)).Success);
        await publisher.RefreshDueAsync(Ct);
        Assert.Equal(0, puts);
    }

    [Fact]
    public async Task BackgroundDiscovery_RunsEveryFifteenMinutes_AndBacksOffWhileNothingIsPublished()
    {
        var start = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc); var now = start;
        var sources = new List<CardShareSource>();
        var gets = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); gets++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(sources, CardSharingJsonContext.Default.ListCardShareSource) });
        }));
        var publisher = new CardSharePublisher(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), new(null, () => now));
        async Task<int> At(int minutes) { now = start.AddMinutes(minutes); await publisher.RefreshDueAsync(Ct); return gets; }
        Assert.Equal(1, await At(0));     // the first tick discovers once
        Assert.Equal(1, await At(1));     // never every minute
        Assert.Equal(1, await At(14));
        Assert.Equal(2, await At(15));    // nothing published: back off to 30
        Assert.Equal(2, await At(44));
        Assert.Equal(3, await At(45));    // 60
        Assert.Equal(3, await At(104));
        Assert.Equal(4, await At(105));   // 120, the ceiling
        Assert.Equal(4, await At(224));
        Assert.Equal(5, await At(225));   // stays at 120
        Assert.Equal(5, await At(344));
        sources.Add(new(1, "VB-OTHER-1", new string('a', 64)));
        Assert.Equal(6, await At(345));   // a publication exists: back to every 15 minutes
        Assert.Equal(6, await At(359));
        Assert.Equal(7, await At(360));
    }

    [Fact]
    public async Task BackgroundRefresh_StopsAfterOneTransfer_WhenTheHostRevisionNeverMatchesUnchangedContent()
    {
        var now = DateTime.UtcNow; var puts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Put) { puts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new CardShareRefreshResult(true, []), CardSharingJsonContext.Default.CardShareRefreshResult) }); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new List<CardShareSource> { new(2, _card.Key, new string('f', 64), _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) });
        }));
        var publisher = new CardSharePublisher(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), new(null, () => now));
        await publisher.RefreshDueAsync(Ct);
        now = now.AddMinutes(15); await publisher.RefreshDueAsync(Ct);
        now = now.AddMinutes(15); await publisher.RefreshDueAsync(Ct);
        Assert.Equal(1, puts);
    }

    [Fact]
    public async Task BackgroundRefresh_ContinuesPastOnePublicationThatFailsUnexpectedly()
    {
        var refreshed = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new List<CardShareSource> { new(2, _card.Key, new string('a', 64), _card.Id), new(3, _card.Key, new string('b', 64), _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) });
            if (request.RequestUri!.AbsolutePath.EndsWith("/sources/2")) throw new HttpRequestException(Key);
            refreshed.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CardShareRefreshResult(true, [_session]), CardSharingJsonContext.Default.CardShareRefreshResult) });
        }));
        await Publisher(http).RefreshDueAsync(Ct);
        Assert.Equal("/api/v1/card-sharing-links/sources/3", Assert.Single(refreshed));
    }

    [Fact]
    public async Task PersistedRefreshState_SurvivesRestart_SoUnchangedCardsAreNotResent_AndHoldsNoSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), "card-share-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var hash = (await Capture().CaptureAsync("project", "card_test", Ct)).Hash;
            var puts = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Put) { puts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = JsonContent.Create(new CardShareRefreshResult(true, []), CardSharingJsonContext.Default.CardShareRefreshResult) }); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                    new List<CardShareSource> { new(2, _card.Key, hash, _card.Id) }, CardSharingJsonContext.Default.ListCardShareSource) });
            }));
            var now = DateTime.UtcNow;
            await new CardSharePublisher(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), new(path, () => now)).RefreshDueAsync(Ct);
            Assert.Equal(1, puts);
            now = now.AddMinutes(15);
            await new CardSharePublisher(_boards.Object, Capture(), new(http), _archives.Object, new(_lockPath), new(path, () => now)).RefreshDueAsync(Ct);
            Assert.Equal(1, puts);
            var stored = await File.ReadAllTextAsync(path, Ct);
            Assert.Contains(hash, stored); Assert.DoesNotContain(Key, stored); Assert.DoesNotContain("shared/card", stored);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CreateWaitsForAShortBoardSync_InsteadOfFailing_AndTransfersWithoutTheLock()
    {
        var lockedDuringTransfer = true;
        using var http = new HttpClient(new Handler(_ =>
        {
            using var probe = new BoardSyncLock(_lockPath).TryAcquire();
            lockedDuringTransfer = probe is null;
            return Task.FromResult(Published());
        }));
        var held = new BoardSyncLock(_lockPath).TryAcquire();
        Assert.NotNull(held);
        var create = Publisher(http).CreateAsync("project", "card_test", "Demo", Ct);
        await Task.Delay(500, Ct);
        Assert.False(create.IsCompleted);
        held.Dispose();
        Assert.True((await create).Success);
        Assert.False(lockedDuringTransfer);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request); }
    public void Dispose()
    {
        ParserConfigs.SetApiKey(_originalKey); ParserConfigs.SetGitState(_originalProject, _originalGit);
        if (File.Exists(_lockPath)) File.Delete(_lockPath);
    }
}
