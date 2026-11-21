using System.Net;
using System.Text;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services.Integrations;

/// <summary>
/// The desktop's read of the account's hosted plan (Pro features). The server is the authority:
/// everything that is not a clear answer is "unknown", which allows every feature.
/// </summary>
public sealed class AccountEntitlementsClientTests
{
    private const string Key = "entitlement-fixture-account-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pro", "pro", true, true)]
    [InlineData("free", "free", true, false)]
    [InlineData("free", "inactive", true, false)]
    [InlineData("free", "free", false, true)]
    public async Task KnownPlansAreReadFromTheConfiguredOriginWithTheAccountKeyInAHeader(string plan, string access, bool enforced, bool allowed)
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://plans.example.test/api/v1/account/entitlements", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Json(Body(plan, access, enforced, allowed));
        });
        var entitlements = Client(handler);

        var snapshot = await entitlements.GetAsync(Ct);

        Assert.Equal(access, snapshot.State);
        Assert.Equal(enforced, snapshot.Enforced);
        Assert.Equal(new ProFeatureFlags(allowed, allowed, allowed, allowed), snapshot.Features);
        Assert.Equal("https://plans.example.test/Billing", snapshot.BillingUrl);
        Assert.NotNull(snapshot.CheckedUtc);
        foreach (var feature in Features)
            Assert.Equal(allowed, await entitlements.AllowsAsync(feature, Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AFeatureCanBeOffWhileTheOthersStayOn()
    {
        var entitlements = Client(new Handler(_ => Json("""
            {"version":1,"plan":"free","access":"free","enforced":true,
             "features":{"remote_boards":false,"remote_terminals":true,"session_sharing":false,"card_sharing":true},
             "billingPath":"/Billing","refreshAfterSeconds":300}
            """)));

        Assert.False(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));
        Assert.True(await entitlements.AllowsAsync(ProFeatures.RemoteTerminals, Ct));
        Assert.False(await entitlements.AllowsAsync(ProFeatures.SessionSharing, Ct));
        Assert.True(await entitlements.AllowsAsync(ProFeatures.CardSharing, Ct));
        // A feature name this version does not know is left to the server.
        Assert.True(await entitlements.AllowsAsync("future_feature", Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAnAccountKeyItIsSignedOut_AndNothingIsSent(string? key)
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Must not send without a key"));
        var entitlements = Client(handler, key: () => key);

        var snapshot = await entitlements.GetAsync(Ct, refresh: true);

        Assert.Equal(AccountEntitlements.SignedOut, snapshot.State);
        Assert.False(snapshot.Enforced);
        Assert.Equal(ProFeatureFlags.None, snapshot.Features);
        Assert.Null(snapshot.CheckedUtc);
        Assert.Equal("https://plans.example.test/Billing", snapshot.BillingUrl);
        Assert.False(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(404)] // an older server without the route
    [InlineData(401)] // the feature's own call reports a rejected key, as before
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task AnyOtherStatusIsUnknown_WhichAllowsEverything(int status)
    {
        var entitlements = Client(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent(Body("free", "free", true, false)) }));
        await AssertUnknownAsync(entitlements);
    }

    [Theory]
    [InlineData("not json " + Key)]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"version":1,"access":"enterprise","enforced":true,"features":{"remote_boards":false,"remote_terminals":false,"session_sharing":false,"card_sharing":false}}""")]
    [InlineData("""{"version":1,"access":"free","enforced":true}""")]
    // A contract version this desktop does not know is not interpreted.
    [InlineData("""{"version":2,"access":"free","enforced":true,"features":{"remote_boards":false,"remote_terminals":false,"session_sharing":false,"card_sharing":false}}""")]
    [InlineData("""{"access":"free","enforced":true,"features":{"remote_boards":false,"remote_terminals":false,"session_sharing":false,"card_sharing":false}}""")]
    // A missing feature is never read as "off".
    [InlineData("""{"version":1,"access":"free","enforced":true,"features":{"remote_boards":false,"remote_terminals":false,"session_sharing":false}}""")]
    [InlineData("""{"version":1,"access":"free","enforced":true,"features":{"remote_boards":"no","remote_terminals":false,"session_sharing":false,"card_sharing":false}}""")]
    public async Task MalformedAnswersAreUnknown(string body)
    {
        await AssertUnknownAsync(Client(new Handler(_ => Json(body))));
    }

    [Fact]
    public async Task OfflineTimeoutAndOversizedAnswersAreUnknown_ButCallerCancellationPropagates()
    {
        await AssertUnknownAsync(Client(new Handler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, Key))));
        // HttpClient reports its own timeout as a cancellation that is not the caller's.
        await AssertUnknownAsync(Client(new Handler(_ => throw new TaskCanceledException("timed out", new TimeoutException()))));

        // The named client's response cap, as the production registration sets it.
        var oversized = new Handler(_ => Json(Body("free", "free", true, false) + new string(' ', AccountEntitlementsClient.MaxResponseBytes)));
        await AssertUnknownAsync(new AccountEntitlementsClient(new Factory(oversized, AccountEntitlementsClient.MaxResponseBytes),
            () => new Uri("https://plans.example.test"), () => Key));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var waiting = Client(new Handler(_ => throw new Xunit.Sdk.XunitException("unreachable")) { Cancellable = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.GetAsync(cancelled.Token));
    }

    [Theory]
    [InlineData("pasted-key-with-a-line-break\n")]
    [InlineData("key\r\nX-Injected: 1")]
    public async Task AKeyThatCannotBeSentAsAHeaderIsUnknown_NeverAnExceptionOrAnInjectedHeader(string key)
    {
        // The plan is asked on the terminal start path and the sync tick: a damaged setting must
        // not turn into a thrown error there.
        var handler = new Handler(request =>
        {
            Assert.False(request.Headers.Contains("X-Injected"));
            return new(HttpStatusCode.Unauthorized);
        });
        await AssertUnknownAsync(Client(handler, key: () => key));
    }

    [Fact]
    public async Task UnknownIsNeverReportedAsAKnownFreeAccount()
    {
        var unknown = await Client(new Handler(_ => new(HttpStatusCode.NotFound))).GetAsync(Ct);
        var free = await Client(new Handler(_ => Json(Body("free", "free", true, false)))).GetAsync(Ct);

        Assert.Equal(AccountEntitlements.Unknown, unknown.State);
        Assert.Equal(AccountEntitlements.Free, free.State);
        Assert.NotEqual(unknown.Features, free.Features);
        Assert.Null(unknown.CheckedUtc);
    }

    [Fact]
    public async Task TheAnswerIsCachedForTheServersLifetime_ThenReadAgain()
    {
        var time = new ManualTime();
        var handler = new Handler(_ => Json(Body("pro", "pro", true, true, refreshAfterSeconds: 120)));
        var entitlements = Client(handler, time: time);

        await entitlements.GetAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.True(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));
        Assert.Equal(1, handler.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        await entitlements.GetAsync(Ct);
        Assert.Equal(2, handler.Calls);

        // A forced read ignores a fresh entry (the user came back from the Billing page).
        await entitlements.GetAsync(Ct, refresh: true);
        Assert.Equal(3, handler.Calls);
        entitlements.Invalidate();
        await entitlements.GetAsync(Ct);
        Assert.Equal(4, handler.Calls);
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData(1, 30)]
    [InlineData(-5, 30)]
    [InlineData(86400, 3600)]
    public async Task TheLifetimeDefaultsToFiveMinutesAndIsClamped(int? refreshAfterSeconds, int expectedSeconds)
    {
        var time = new ManualTime();
        var handler = new Handler(_ => Json(Body("pro", "pro", true, true, refreshAfterSeconds)));
        var entitlements = Client(handler, time: time);

        await entitlements.GetAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(expectedSeconds - 1));
        await entitlements.GetAsync(Ct);
        Assert.Equal(1, handler.Calls);
        time.Advance(TimeSpan.FromSeconds(2));
        await entitlements.GetAsync(Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AnUnknownAnswerIsAskedAgainSooner()
    {
        var time = new ManualTime();
        var handler = new Handler(_ => new(HttpStatusCode.ServiceUnavailable));
        var entitlements = Client(handler, time: time);

        await entitlements.GetAsync(Ct);
        await entitlements.GetAsync(Ct);
        Assert.Equal(1, handler.Calls); // not once per caller while the server is down
        time.Advance(AccountEntitlementsClient.UnknownLifetime + TimeSpan.FromSeconds(1));
        await entitlements.GetAsync(Ct);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TheCacheIsKeyedByAccountKeyAndOrigin_SoAReplacedKeyNeedsNoHook()
    {
        var key = "first-account-key";
        var origin = new Uri("https://plans.example.test");
        var seen = new List<string>();
        var handler = new Handler(request =>
        {
            var sent = Assert.Single(request.Headers.GetValues("X-Api-Key"));
            seen.Add(request.RequestUri!.Host + " " + sent);
            return Json(Body("free", sent == "first-account-key" ? "free" : "pro", true, sent != "first-account-key"));
        });
        var entitlements = new AccountEntitlementsClient(new Factory(handler), () => origin, () => key);

        Assert.False(await entitlements.AllowsAsync(ProFeatures.CardSharing, Ct));
        key = "second-account-key"; // linked another account
        Assert.True(await entitlements.AllowsAsync(ProFeatures.CardSharing, Ct));
        key = "first-account-key"; // and back: its own entry is still fresh
        Assert.False(await entitlements.AllowsAsync(ProFeatures.CardSharing, Ct));
        origin = new Uri("https://other.example.test"); // the same key against another server
        Assert.False(await entitlements.AllowsAsync(ProFeatures.CardSharing, Ct));
        key = ""; // signed out
        Assert.Equal(AccountEntitlements.SignedOut, (await entitlements.GetAsync(Ct)).State);

        Assert.Equal(["plans.example.test first-account-key", "plans.example.test second-account-key", "other.example.test first-account-key"], seen);
    }

    [Fact]
    public async Task ADenialIsNotedAtOnce_ForThatFeatureOnly_UntilTheNextRead()
    {
        var time = new ManualTime();
        var handler = new Handler(_ => Json(Body("pro", "pro", false, true)));
        var entitlements = Client(handler, time: time);
        Assert.True(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));

        entitlements.NoteDenied(ProFeatures.RemoteBoards);

        var noted = await entitlements.GetAsync(Ct);
        Assert.Equal(AccountEntitlements.Free, noted.State); // no longer reported as Pro
        Assert.True(noted.Enforced);
        Assert.False(noted.Allows(ProFeatures.RemoteBoards));
        Assert.True(noted.Allows(ProFeatures.RemoteTerminals));
        Assert.True(noted.Allows(ProFeatures.SessionSharing));
        Assert.True(noted.Allows(ProFeatures.CardSharing));
        Assert.Equal(1, handler.Calls); // background loops stop without another request

        // A forced read (or the lifetime passing) restores the server's own answer.
        time.Advance(AccountEntitlementsClient.DefaultLifetime + TimeSpan.FromSeconds(1));
        Assert.True(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ADenialOnAnUnknownOrUnreadAccountStillStopsThatFeature_AndKeepsInactiveAccountsInactive()
    {
        var unread = Client(new Handler(_ => throw new Xunit.Sdk.XunitException("A noted denial needs no request")));
        unread.NoteDenied(ProFeatures.RemoteTerminals);
        Assert.False(await unread.AllowsAsync(ProFeatures.RemoteTerminals, Ct));
        Assert.True(await unread.AllowsAsync(ProFeatures.RemoteBoards, Ct));
        Assert.Equal(AccountEntitlements.Free, (await unread.GetAsync(Ct)).State);

        var inactive = Client(new Handler(_ => Json(Body("pro", "inactive", false, true))));
        await inactive.GetAsync(Ct);
        inactive.NoteDenied(ProFeatures.CardSharing);
        Assert.Equal(AccountEntitlements.Inactive, (await inactive.GetAsync(Ct)).State);

        // Signed out: nothing to note, nothing recorded for a later account.
        var key = "";
        var later = new AccountEntitlementsClient(new Factory(new Handler(_ => Json(Body("pro", "pro", true, true)))),
            () => new Uri("https://plans.example.test"), () => key);
        later.NoteDenied(ProFeatures.RemoteBoards);
        key = Key;
        Assert.True(await later.AllowsAsync(ProFeatures.RemoteBoards, Ct));
    }

    [Theory]
    [InlineData("https://viberails.example.test", "https://viberails.example.test/")]
    [InlineData("https://viberails.example.test/some/path?x=1", "https://viberails.example.test/")]
    [InlineData("http://127.0.0.1:5123", "http://127.0.0.1:5123/")]
    [InlineData("http://viberails.example.test", null)] // the key rides in a header: HTTPS only
    [InlineData("https://user:secret@viberails.example.test", null)]
    [InlineData("ftp://viberails.example.test", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheOriginIsHttpsOrLoopback_WithoutCredentials(string? configured, string? expected)
    {
        Assert.Equal(expected, AccountEntitlementsClient.Origin(configured)?.AbsoluteUri);
    }

    [Fact]
    public async Task WithoutAUsableOriginItIsUnknown_AndNothingIsSent()
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Must not send without an origin"));
        var entitlements = new AccountEntitlementsClient(new Factory(handler), () => null, () => Key);

        var snapshot = await entitlements.GetAsync(Ct);

        Assert.Equal(AccountEntitlements.Unknown, snapshot.State);
        Assert.Equal(ProFeatureFlags.All, snapshot.Features);
        Assert.Equal("https://viberails.ai/Billing", snapshot.BillingUrl);
        entitlements.NoteDenied(ProFeatures.RemoteBoards);
        Assert.True(await entitlements.AllowsAsync(ProFeatures.RemoteBoards, Ct));
    }

    [Fact]
    public async Task TheSnapshotNeverCarriesTheKeyOrRemoteProse()
    {
        var snapshot = await Client(new Handler(_ => Json("""
            {"version":1,"plan":"free","access":"free","enforced":true,"note":"REMOTE PROSE",
             "features":{"remote_boards":false,"remote_terminals":false,"session_sharing":false,"card_sharing":false},
             "billingPath":"https://evil.example.test/steal","refreshAfterSeconds":300}
            """))).GetAsync(Ct);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, VibeRails.DTOs.AppJsonSerializerContext.Default.AccountEntitlements);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain("REMOTE PROSE", json);
        Assert.DoesNotContain("evil.example.test", json); // the billing link is always this origin's /Billing
        Assert.Contains("\"billingUrl\":\"https://plans.example.test/Billing\"", json);
    }

    private static readonly string[] Features =
        [ProFeatures.RemoteBoards, ProFeatures.RemoteTerminals, ProFeatures.SessionSharing, ProFeatures.CardSharing];

    private static async Task AssertUnknownAsync(AccountEntitlementsClient entitlements)
    {
        var snapshot = await entitlements.GetAsync(Ct);
        Assert.Equal(AccountEntitlements.Unknown, snapshot.State);
        Assert.False(snapshot.Enforced);
        Assert.Equal(ProFeatureFlags.All, snapshot.Features);
        foreach (var feature in Features)
            Assert.True(await entitlements.AllowsAsync(feature, Ct));
    }

    private static AccountEntitlementsClient Client(Handler handler, Func<string?>? key = null, TimeProvider? time = null) =>
        new(new Factory(handler), () => new Uri("https://plans.example.test"), key ?? (() => Key), time);

    private static string Body(string plan, string access, bool enforced, bool allowed, int? refreshAfterSeconds = 300)
    {
        var flag = allowed ? "true" : "false";
        var refresh = refreshAfterSeconds is { } seconds ? $",\"refreshAfterSeconds\":{seconds}" : "";
        return $$"""
            {"version":1,"plan":"{{plan}}","access":"{{access}}","enforced":{{(enforced ? "true" : "false")}},
             "features":{"remote_boards":{{flag}},"remote_terminals":{{flag}},"session_sharing":{{flag}},"card_sharing":{{flag}}},
             "billingPath":"/Billing"{{refresh}}}
            """;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Factory(Handler handler, int? maxResponseBytes = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(AccountEntitlementsClient.HttpClientName, name);
            var client = new HttpClient(handler, disposeHandler: false);
            if (maxResponseBytes is { } bytes) client.MaxResponseContentBufferSize = bytes;
            return client;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public bool Cancellable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Cancellable) return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }
}
