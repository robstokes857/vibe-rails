using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services.Integrations;

public sealed class RemoteAccountLinkServiceTests
{
    private const string DeviceSecret = "ddddddddddddddddddddddddddddddddddddddddddd";
    private const string ApiKey = "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";

    [Theory]
    [InlineData("http://viberails.ai/")]
    [InlineData("http://localhost.evil.example/")]
    [InlineData("https://user:secret@viberails.ai/")]
    [InlineData("https://viberails.ai/?token=secret")]
    [InlineData("https://viberails.ai/#secret")]
    [InlineData("https://viberails.ai/another/path")]
    [InlineData("file:///C:/test")]
    public void UnsafeEndpointIsRejectedBeforeNetworkAccess(string endpoint)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send.")));
        Assert.ThrowsAny<ArgumentException>(() => new RemoteAccountLinkService(
            client, new Uri(endpoint), new KeyStore(), new AppEventBus()));
    }

    [Fact]
    public async Task StartExposesOnlyThePublicCodeAndSendsNoExistingCredential()
    {
        using var fixture = new Fixture();
        fixture.Keys.Value = "previous-private-api-key";

        var status = await fixture.Service.StartAsync();

        Assert.Equal("pending", status.Status);
        Assert.Equal("BXQK-2M7T", status.UserCode);
        Assert.Equal("https://viberails.ai/link", status.VerificationUri);
        Assert.NotNull(status.ExpiresAt);
        Assert.InRange(status.ExpiresAt.Value, fixture.Clock.Now.AddSeconds(590), fixture.Clock.Now.AddSeconds(610));
        var sent = Assert.Single(fixture.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://viberails.ai/api/v1/device-links", sent.Uri.AbsoluteUri);
        Assert.DoesNotContain(fixture.Keys.Value, sent.Body);
        Assert.False(sent.HadAuthorization);
        Assert.Contains("Test workstation", sent.Body);
        AssertNoSecrets(JsonSerializer.Serialize(status));
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Theory]
    [InlineData("https://evil.example/link")]
    [InlineData("http://viberails.ai/link")]
    [InlineData("https://viberails.ai/link?code=secret")]
    [InlineData("https://viberails.ai/link#secret")]
    [InlineData("https://user:secret@viberails.ai/link")]
    [InlineData("https://viberails.ai/Account/Login")]
    public async Task UntrustedVerificationDestinationNeverReachesTheBrowser(string verificationUri)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Created(verificationUri)));

        var status = await fixture.Service.StartAsync();

        Assert.Equal("error", status.Status);
        Assert.Equal("invalid_response", status.Error);
        Assert.Null(status.VerificationUri);
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "unavailable", "old_server")]
    [InlineData(HttpStatusCode.InternalServerError, "error", "remote_error")]
    [InlineData(HttpStatusCode.Redirect, "error", "remote_error")]
    public async Task StartFailureUsesSafeLocalErrorCodes(HttpStatusCode code, string expectedStatus, string expectedError)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Json(code, "{\"error\":\"REMOTE PRIVATE ERROR\"}")));

        var status = await fixture.Service.StartAsync();

        Assert.Equal(expectedStatus, status.Status);
        Assert.Equal(expectedError, status.Error);
        Assert.DoesNotContain("REMOTE PRIVATE ERROR", JsonSerializer.Serialize(status));
        Assert.Null(status.UserCode);
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"deviceCode\":null,\"userCode\":\"BXQK-2M7T\",\"verificationUri\":\"https://viberails.ai/link\",\"expiresIn\":600,\"interval\":3}")]
    public async Task InvalidCreateResponseFailsWithoutPersistingAnything(string body)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));
        var status = await fixture.Service.StartAsync();
        Assert.Equal("error", status.Status);
        Assert.Equal("invalid_response", status.Error);
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedCreateResponseIsBoundedWithAndWithoutContentLength(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1));
        using var fixture = new Fixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(knownLength ? new MemoryStream(bytes) : new NonSeekableStream(bytes))
            {
                Headers = { ContentLength = knownLength ? bytes.Length : null }
            }
        }));

        var status = await fixture.Service.StartAsync();

        Assert.Equal("error", status.Status);
        Assert.Equal("invalid_response", status.Error);
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Fact]
    public async Task PollRespectsTheIntervalAndKeepsDeviceSecretInRequestBody()
    {
        using var fixture = new Fixture();
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var first = await fixture.Service.PollAsync();
        var second = await fixture.Service.PollAsync();

        Assert.Equal("pending", first.Status);
        Assert.Equal("pending", second.Status);
        var token = Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Empty(token.Uri.Query);
        Assert.False(token.HadAuthorization);
        using var body = JsonDocument.Parse(token.Body);
        Assert.Equal(DeviceSecret, body.RootElement.GetProperty("deviceCode").GetString());
        AssertNoSecrets(JsonSerializer.Serialize(second));
    }

    [Fact]
    public async Task ConcurrentPollsMakeOneRemoteRequestAndSaveOneKey()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(request =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/token")) return Task.FromResult(Created());
            entered.SetResult();
            return release.Task;
        });
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var first = fixture.Service.PollAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = fixture.Service.PollAsync();
        release.SetResult(Approved());
        await Task.WhenAll(first, second);

        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        Assert.Equal(1, fixture.Keys.SaveCalls);
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Single(fixture.Events);
        Assert.Equal("linked", (await fixture.Service.PollAsync()).Status);
    }

    [Fact]
    public async Task SuccessfulLinkPublishesOnlyMaskedAccountDetailsAndDoesNotRedeemAgain()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var status = await fixture.Service.PollAsync();
        await fixture.Service.PollAsync();

        Assert.Equal("linked", status.Status);
        Assert.Equal("test@example.test", status.Account?.Email);
        Assert.NotNull(status.KeyHint);
        AssertNoSecrets(JsonSerializer.Serialize(status));
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Equal(1, fixture.Keys.SaveCalls);
        var appEvent = Assert.Single(fixture.Events);
        Assert.Equal("remote-account-linked", appEvent.Type);
        AssertNoSecrets(JsonSerializer.Serialize(appEvent));
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
    }

    [Fact]
    public async Task ReplacingTheSavedCredentialClearsThePreviouslyLinkedIdentity()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("linked", (await fixture.Service.PollAsync()).Status);

        fixture.Keys.Value = "a-different-manually-pasted-key";
        var status = await fixture.Service.PollAsync();

        Assert.Equal("idle", status.Status);
        Assert.Null(status.Account);
        Assert.Null(status.KeyHint);
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
    }

    [Fact]
    public async Task BrokenEventSubscriberCannotTurnPersistedCredentialIntoFailedLogin()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        using var broken = fixture.Bus.Subscribe(_ => throw new InvalidOperationException("Broken subscriber"));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var status = await fixture.Service.PollAsync();

        Assert.Equal("linked", status.Status);
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Equal(1, fixture.Keys.SaveCalls);
    }

    [Fact]
    public async Task FailedLocalSaveRetainsTheReceivedKeyForRetryWithoutSecondRedemption()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        fixture.Keys.FailNextSave = true;
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var failed = await fixture.Service.PollAsync();
        Assert.Equal("save_failed", failed.Error);
        Assert.Empty(fixture.Events);
        var retried = await fixture.Service.PollAsync();

        Assert.Equal("linked", retried.Status);
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Equal(2, fixture.Keys.SaveCalls);
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        Assert.Single(fixture.Events);
    }

    [Fact]
    public async Task AReceivedKeyRemainsRetryableWhenRemoteAttemptExpiresDuringLocalSaveFailure()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        fixture.Keys.FailNextSave = true;
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(599));
        Assert.Equal("save_failed", (await fixture.Service.PollAsync()).Error);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await fixture.Service.PollAsync();

        Assert.Equal("linked", retried.Status);
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
    }

    [Fact]
    public async Task SlowDownIncreasesPollingIntervalAndPreventsEarlyRetry()
    {
        using var fixture = new Fixture(request => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/token")
            ? Json(HttpStatusCode.TooManyRequests, "{\"error\":\"slow_down\"}") : Created()));
        var start = await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var slowed = await fixture.Service.PollAsync();
        Assert.Equal("pending", slowed.Status);
        Assert.True(slowed.Interval > start.Interval);
        fixture.Clock.Advance(TimeSpan.FromSeconds(start.Interval));
        await fixture.Service.PollAsync();
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(slowed.Interval));
        await fixture.Service.PollAsync();
        Assert.Equal(2, fixture.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token")));
    }

    [Fact]
    public async Task TransientTokenFailureKeepsAttemptForRetryAndNeverExposesRemoteError()
    {
        var polls = 0;
        using var fixture = new Fixture(request =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/token")) return Task.FromResult(Created());
            if (++polls == 1) throw new HttpRequestException("PRIVATE remote diagnostic");
            return Task.FromResult(Approved());
        });
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var failed = await fixture.Service.PollAsync();
        Assert.Equal("pending", failed.Status);
        Assert.Equal("remote_error", failed.Error);
        Assert.DoesNotContain("PRIVATE remote diagnostic", JsonSerializer.Serialize(failed));
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("linked", (await fixture.Service.PollAsync()).Status);
        Assert.Equal(ApiKey, fixture.Keys.Value);
    }

    [Fact]
    public async Task CredentialChangedDuringLoginIsNeverOverwritten()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        fixture.Keys.Value = "previous-api-key";
        await fixture.Service.StartAsync();
        fixture.Keys.Value = "manually-replaced-api-key";
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var status = await fixture.Service.PollAsync();

        Assert.Equal("key_changed", status.Error);
        Assert.Equal("manually-replaced-api-key", fixture.Keys.Value);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task CancelDuringTokenRequestPreventsLateCredentialSave()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/token"))
            {
                entered.SetResult();
                return release.Task;
            }
            return Task.FromResult(request.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent) : Created());
        });
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        var poll = fixture.Service.PollAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var canceled = await fixture.Service.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult(Approved());
        await poll;

        Assert.Equal("cancelled", canceled.Status);
        Assert.Equal(0, fixture.Keys.SaveCalls);
        Assert.Empty(fixture.Events);
        Assert.Equal("cancelled", (await fixture.Service.PollAsync()).Status);
        var delete = Assert.Single(fixture.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Empty(delete.Uri.Query);
        Assert.Contains(DeviceSecret, delete.Body);
    }

    [Fact]
    public async Task CancelAfterTheKeyWasSavedReportsLinkedWithoutDeletingOrChangingTheCredential()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        var linked = await fixture.Service.PollAsync();

        // The browser can still show Pending when the token request saved the key but its
        // response was lost. Cancel must report the completed save, not imply it was undone.
        var canceled = await fixture.Service.CancelAsync();

        Assert.Equal("linked", canceled.Status);
        Assert.Equal(linked.KeyHint, canceled.KeyHint);
        Assert.Equal(ApiKey, fixture.Keys.Value);
        Assert.Equal(1, fixture.Keys.SaveCalls);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Single(fixture.Events);
    }

    [Fact]
    public async Task CancelDoesNotReportAnAccountWhoseKeyWasManuallyReplaced()
    {
        using var fixture = new Fixture(request => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/token") ? Approved() : Created()));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        await fixture.Service.PollAsync();
        fixture.Keys.Value = "manually-replaced-api-key";

        var status = await fixture.Service.CancelAsync();

        Assert.Equal("cancelled", status.Status);
        Assert.Null(status.Account);
        Assert.Null(status.KeyHint);
        Assert.Equal("manually-replaced-api-key", fixture.Keys.Value);
    }

    [Fact]
    public async Task NewStartReplacesInFlightAttemptWithoutSavingItsLateKey()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var fixture = new Fixture(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/token"))
            {
                entered.SetResult();
                return release.Task;
            }
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(Created(userCode: ++starts == 1 ? "BXQK-2M7T" : "CPRS-3N8V"));
        });
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        var oldPoll = fixture.Service.PollAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var replacement = await fixture.Service.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult(Approved());
        await oldPoll;

        Assert.Equal("CPRS-3N8V", replacement.UserCode);
        Assert.Equal(0, fixture.Keys.SaveCalls);
        Assert.Empty(fixture.Events);
        Assert.Equal("CPRS-3N8V", (await fixture.Service.PollAsync()).UserCode);
    }

    [Fact]
    public async Task ExpiredAttemptDoesNotContactTokenEndpoint()
    {
        using var fixture = new Fixture();
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));

        var status = await fixture.Service.PollAsync();

        Assert.Equal("expired", status.Status);
        Assert.Equal("expired_token", status.Error);
        Assert.DoesNotContain(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access_denied", "denied")]
    [InlineData(HttpStatusCode.Gone, "expired_token", "expired")]
    public async Task RemoteTerminalStateStopsFurtherPolling(HttpStatusCode code, string error, string expectedStatus)
    {
        using var fixture = new Fixture(request => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/token")
            ? Json(code, JsonSerializer.Serialize(new { error, detail = "REMOTE PRIVATE DETAIL" })) : Created()));
        await fixture.Service.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));

        var status = await fixture.Service.PollAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        await fixture.Service.PollAsync();

        Assert.Equal(expectedStatus, status.Status);
        Assert.Equal(error, status.Error);
        Assert.DoesNotContain("REMOTE PRIVATE DETAIL", JsonSerializer.Serialize(status));
        Assert.Single(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/token"));
        Assert.Equal(0, fixture.Keys.SaveCalls);
    }

    private static void AssertNoSecrets(string json)
    {
        Assert.DoesNotContain(DeviceSecret, json);
        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain("deviceCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Created(string verificationUri = "https://viberails.ai/link", string userCode = "BXQK-2M7T") =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { deviceCode = DeviceSecret, userCode, verificationUri, expiresIn = 600, interval = 3 }));

    private static HttpResponseMessage Approved() => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        apiKey = ApiKey, keyPrefix = "kkkk", keySuffix = "kkkk", account = new { email = "test@example.test", name = "Test Account" }
    }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly IDisposable _subscription;
        public KeyStore Keys { get; } = new();
        public ManualClock Clock { get; } = new();
        public List<Request> Requests { get; } = [];
        public List<AppEvent> Events { get; } = [];
        public AppEventBus Bus { get; } = new();
        public RemoteAccountLinkService Service { get; }

        public Fixture(Func<Request, Task<HttpResponseMessage>>? respond = null)
        {
            _client = new HttpClient(new Handler(async request =>
            {
                Requests.Add(request);
                return respond is not null ? await respond(request) : request.Uri.AbsolutePath.EndsWith("/token")
                    ? Json(HttpStatusCode.Accepted, "{\"error\":\"authorization_pending\"}") : Created();
            }));
            _subscription = Bus.Subscribe(Events.Add);
            Service = new RemoteAccountLinkService(_client, new Uri("https://viberails.ai/"), Keys, Bus, Clock);
        }

        public void Dispose()
        {
            Service.Dispose();
            _subscription.Dispose();
            _client.Dispose();
        }
    }

    private sealed class KeyStore : IRemoteAccountKeyStore
    {
        public string Value { get; set; } = "";
        public bool FailNextSave { get; set; }
        public int SaveCalls { get; private set; }
        public string Read() => Value;
        public string ComputerName => "Test workstation";
        public bool TrySave(string apiKey, string expectedApiKey)
        {
            SaveCalls++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("PRIVATE filesystem detail");
            }
            if (!string.Equals(Value, expectedApiKey, StringComparison.Ordinal)) return false;
            Value = apiKey;
            return true;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now += duration;
    }

    private sealed record Request(HttpMethod Method, Uri Uri, string Body, bool HadAuthorization);

    private sealed class Handler(Func<Request, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            await respond(new Request(request.Method, request.RequestUri!,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                request.Headers.Contains("Authorization") || request.Headers.Contains("X-Api-Key") || request.Headers.Contains("Cookie")));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
