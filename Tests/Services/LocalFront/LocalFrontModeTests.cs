using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails;
using VibeRails.DTOs;
using VibeRails.Jobs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.LocalFront;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LocalFront;

/// <summary>Which processes enter local Front mode, and which origins it accepts (VB-8NI09-170).</summary>
public sealed class LocalFrontModeResolveTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetVariableIsANormalProcess(string? raw)
    {
        var state = LocalFrontMode.Resolve(raw, debugBuild: true, "Development");

        Assert.False(state.Requested);
        Assert.False(state.Active);
    }

    [Fact]
    public void ADebugDevelopmentProcessWithTheDefaultOriginIsActive()
    {
        var state = LocalFrontMode.Resolve("https://localhost:5164", debugBuild: true, "Development");

        Assert.True(state.Active);
        Assert.Equal("https://localhost:5164", state.OriginText);
        Assert.Equal("https://localhost:5164/", state.Origin!.AbsoluteUri);
    }

    [Fact]
    public void AReleaseBuildRefusesButStillCountsAsRequested()
    {
        var state = LocalFrontMode.Resolve("https://localhost:5164", debugBuild: false, "Development");

        Assert.True(state.Requested);
        Assert.False(state.Active);
        Assert.Contains("Debug build", state.Error);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void ANonDevelopmentEnvironmentRefuses(string? environment)
    {
        var state = LocalFrontMode.Resolve("https://localhost:5164", debugBuild: true, environment);

        Assert.True(state.Requested);
        Assert.False(state.Active);
        Assert.Contains("Development", state.Error);
    }

    [Theory]
    [InlineData("https://localhost:5164", "https://localhost:5164")]
    [InlineData("https://localhost:5164/", "https://localhost:5164")]
    [InlineData("https://LOCALHOST:5164", "https://localhost:5164")]
    [InlineData("https://127.0.0.1:5164", "https://127.0.0.1:5164")]
    [InlineData("https://[::1]:5164", "https://[::1]:5164")]
    [InlineData("https://localhost", "https://localhost")]
    public void LoopbackHttpsOriginsAreNormalized(string raw, string expected)
    {
        Assert.True(LocalFrontMode.TryParseOrigin(raw, out var origin, out var error), error);
        Assert.Equal(expected, origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/", origin.AbsolutePath);
    }

    [Theory]
    [InlineData("http://localhost:5164")]
    [InlineData("https://viberails.ai")]
    [InlineData("https://localhost.evil.example:5164")]
    [InlineData("https://10.0.0.5:5164")]
    [InlineData("https://user:secret@localhost:5164")]
    [InlineData("https://localhost:5164/api")]
    [InlineData("https://localhost:5164/?x=1")]
    [InlineData("https://localhost:5164/#x")]
    [InlineData("https://localhost:5164?")]
    [InlineData(@"https:\\localhost:5164")]
    [InlineData("https://local host:5164")]
    [InlineData("localhost:5164")]
    [InlineData("wss://localhost:5164")]
    public void NonLocalOrMalformedOriginsFailClosed(string raw)
    {
        Assert.False(LocalFrontMode.TryParseOrigin(raw, out var origin, out var error));
        Assert.Null(origin);
        Assert.Contains(LocalFrontMode.OriginVariable, error);

        var state = LocalFrontMode.Resolve(raw, debugBuild: true, "Development");
        Assert.True(state.Requested);
        Assert.False(state.Active);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://viberails.ai", true)]
    [InlineData("https://viberails.ai/", true)]
    [InlineData("https://localhost:5164", true)]
    [InlineData("https://staging.example", false)]
    [InlineData("https://localhost:6000", false)]
    public void OnlyTheShippedOrLocalFrontendUrlIsCompatible(string? configured, bool expected)
    {
        var state = LocalFrontMode.Resolve("https://localhost:5164", debugBuild: true, "Development");

        Assert.Equal(expected, LocalFrontMode.IsCompatibleFrontendSetting(configured, state));
    }

    [Fact]
    public void TheTestProcessItselfIsNeverInLocalMode()
    {
        // Tests run with no local-front variable (terminal tabs remove it from PTY shells).
        Assert.False(LocalFrontMode.Current.Requested);
    }
}

/// <summary>The tripwire every factory client gets in a local Front process.</summary>
public sealed class ProductionFrontTripwireTests
{
    [Theory]
    [InlineData("https://viberails.ai/api/v1/data-exports", true)]
    [InlineData("https://VIBERAILS.AI/api", true)]
    [InlineData("https://www.viberails.ai/", true)]
    [InlineData("https://viberails.ai./", true)]
    [InlineData("https://localhost:5164/api/v1/terminal", false)]
    [InlineData("https://api.anthropic.com/v1/messages", false)]
    [InlineData("https://notviberails.ai/", false)]
    [InlineData("https://viberails.ai.example.com/", false)]
    public void RecognisesOnlyProductionHosts(string url, bool expected)
    {
        Assert.Equal(expected, ProductionFrontTripwireHandler.IsProductionHost(new Uri(url)));
    }

    [Fact]
    public async Task BlocksAProductionRequestBeforeItLeaves()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new ProductionFrontTripwireHandler { InnerHandler = inner });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("https://viberails.ai/api/v1/token-savings", TestContext.Current.CancellationToken));

        Assert.Contains("Local Front mode blocked", ex.Message);
        Assert.Equal(0, inner.Calls);

        await client.GetAsync("https://localhost:5164/dev/login", TestContext.Current.CancellationToken);
        Assert.Equal(1, inner.Calls);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

/// <summary>The local key file never shares anything with settings.json.</summary>
public sealed class LocalFrontKeyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails-local-front-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void KeysAreScopedToTheNormalizedOrigin()
    {
        var store = new LocalFrontKeyStore(Path.Combine(_directory, "local-front-keys.json"));

        store.Set("https://localhost:5164/", "local-key-one", "one@local.test");

        Assert.Equal("local-key-one", store.Read("https://localhost:5164")?.ApiKey);
        Assert.Equal("one@local.test", store.Read("https://LOCALHOST:5164")?.AccountEmail);
        Assert.Null(store.Read("https://localhost:6000"));
        Assert.Throws<ArgumentException>(() => store.Read("https://viberails.ai"));
    }

    [Fact]
    public void ReplaceComparesTheSavedKeyAndBlankRemoves()
    {
        var store = new LocalFrontKeyStore(Path.Combine(_directory, "local-front-keys.json"));

        Assert.True(store.TryReplace("https://localhost:5164", "first", expectedApiKey: "", accountEmail: null));
        Assert.False(store.TryReplace("https://localhost:5164", "second", expectedApiKey: "stale", accountEmail: null));
        Assert.Equal("first", store.Read("https://localhost:5164")?.ApiKey);
        Assert.True(store.TryReplace("https://localhost:5164", "", expectedApiKey: "first", accountEmail: null));
        Assert.Null(store.Read("https://localhost:5164"));
    }
}

/// <summary>
/// Settings, sign-in, sync and uploads in a local Front process. These override
/// <see cref="LocalFrontMode.Current"/> for their own async flow and set ParserConfigs.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class LocalFrontProcessTests : IDisposable
{
    private const string ProductionKey = "prod-key-0000000000000000000000000001";
    private const string LocalKey = "local-key-000000000000000000000000002";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails-local-front-{Guid.NewGuid():N}");
    private readonly IDisposable _mode = LocalFrontMode.UseForTests(
        LocalFrontMode.Resolve(LocalFrontMode.DefaultOrigin, debugBuild: true, "Development"));
    private readonly string _apiKey = ParserConfigs.GetApiKey();
    private readonly bool _remoteAccess = ParserConfigs.GetRemoteAccess();
    private readonly bool _relay = ParserConfigs.GetRouteThroughVibeRailsAi();
    private readonly bool _theme = ParserConfigs.GetUseVsCodeTheme();

    public void Dispose()
    {
        _mode.Dispose();
        ParserConfigs.SetApiKey(_apiKey);
        ParserConfigs.SetRemoteAccess(_remoteAccess);
        ParserConfigs.SetRouteThroughVibeRailsAi(_relay);
        ParserConfigs.SetUseVsCodeTheme(_theme);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private (SettingsFile Settings, LocalFrontKeyStore Keys) Fixture()
    {
        var settings = new SettingsFile(Path.Combine(_directory, "settings.json"));
        settings.Save(new Settings
        {
            ApiKey = ProductionKey,
            RemoteAccountEmail = "rob@production.test",
            RemoteAccountKeyFingerprint = "PRODUCTION-FINGERPRINT",
            RouteThroughVibeRailsAi = true
        });
        return (settings, new LocalFrontKeyStore(Path.Combine(_directory, "local-front-keys.json")));
    }

    [Fact]
    public void SettingsShowAndEditOnlyTheLocalKey()
    {
        var (settings, keys) = Fixture();
        using var _ = settings;

        var shown = AppSettingsRoutes.BuildAppSettingsDto(settings.LoadFresh(), keys);
        Assert.Equal("", shown.ApiKey);
        Assert.Null(shown.RemoteAccountEmail);
        Assert.Equal("https://localhost:5164", shown.LocalFrontOrigin);

        var saved = AppSettingsRoutes.UpdateSettings(shown with { ApiKey = LocalKey }, settings, relay: null, keys);

        Assert.EndsWith(LocalKey[^4..], saved.ApiKey);
        Assert.DoesNotContain(ProductionKey[^4..], saved.ApiKey);
        Assert.Equal(LocalKey, keys.Read(LocalFrontMode.DefaultOrigin)?.ApiKey);
        Assert.Equal(LocalKey, ParserConfigs.GetApiKey());
        var production = settings.LoadFresh();
        Assert.Equal(ProductionKey, production.ApiKey);
        Assert.Equal("rob@production.test", production.RemoteAccountEmail);
        Assert.Equal("PRODUCTION-FINGERPRINT", production.RemoteAccountKeyFingerprint);
        Assert.True(production.RouteThroughVibeRailsAi);
        Assert.DoesNotContain(LocalKey, File.ReadAllText(Path.Combine(_directory, "settings.json")));

        var cleared = AppSettingsRoutes.UpdateSettings(saved with { ApiKey = "", ClearApiKey = true }, settings, relay: null, keys);

        Assert.Equal("", cleared.ApiKey);
        Assert.Null(keys.Read(LocalFrontMode.DefaultOrigin));
        Assert.Equal("", ParserConfigs.GetApiKey());
        Assert.Equal(ProductionKey, settings.LoadFresh().ApiKey);
    }

    [Fact]
    public async Task SignInPairsWithTheLocalOriginAndSavesOnlyTheLocalKey()
    {
        var (settings, keys) = Fixture();
        using var _ = settings;
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/device-links" => Json("""{"deviceCode":"dddddddddddddddddddddddddddddddddddddddddd","userCode":"BXQK-2M7T","verificationUri":"https://localhost:5164/link","expiresIn":600,"interval":1}"""),
                "/api/v1/device-links/token" => Json("""{"apiKey":"KEY","account":{"email":"local|one@local.test"}}""".Replace("KEY", LocalKey)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var clock = new ManualClock();
        using var linker = new RemoteAccountLinkService(client, LocalFrontMode.Current.Origin!,
            new LocalFrontAccountKeyStore(keys, LocalFrontMode.DefaultOrigin, new NullRelay()), new AppEventBus(), clock);

        var pending = await linker.StartAsync();
        Assert.Equal("pending", pending.Status);
        Assert.Equal("https://localhost:5164/link", pending.VerificationUri);
        clock.Advance(TimeSpan.FromSeconds(2));
        var linked = await linker.PollAsync();

        Assert.Equal("linked", linked.Status);
        Assert.All(requests, uri => Assert.Equal("https://localhost:5164", uri.GetLeftPart(UriPartial.Authority)));
        Assert.Equal(LocalKey, keys.Read(LocalFrontMode.DefaultOrigin)?.ApiKey);
        Assert.Equal(LocalKey, ParserConfigs.GetApiKey());
        Assert.Equal(ProductionKey, settings.LoadFresh().ApiKey);
    }

    [Fact]
    public async Task SignInRefusesAVerificationPageOnAnotherOrigin()
    {
        var (settings, keys) = Fixture();
        using var _ = settings;
        using var client = new HttpClient(new StubHandler(_ =>
            Json("""{"deviceCode":"dddddddddddddddddddddddddddddddddddddddddd","userCode":"BXQK-2M7T","verificationUri":"https://viberails.ai/link","expiresIn":600,"interval":1}""")));
        using var linker = new RemoteAccountLinkService(client, LocalFrontMode.Current.Origin!,
            new LocalFrontAccountKeyStore(keys, LocalFrontMode.DefaultOrigin, new NullRelay()), new AppEventBus());

        var status = await linker.StartAsync();

        Assert.Equal("error", status.Status);
        Assert.Equal("invalid_response", status.Error);
        Assert.Null(keys.Read(LocalFrontMode.DefaultOrigin));
    }

    [Fact]
    public async Task BoardSyncAndSharingStopBeforeTouchingAnything()
    {
        // Null dependencies: any store, lock or client access would throw NullReferenceException.
        var sync = new BoardSyncService(null!, null!, null!, null!, null!);
        var sharing = new BoardSharingService(null!, null!, sync, null!);
        var ct = TestContext.Current.CancellationToken;

        var publish = await Assert.ThrowsAsync<BoardValidationException>(() => sync.SetPublishedAsync("p", "b", true, ct));
        Assert.Contains("local Front", publish.Message);
        await Assert.ThrowsAsync<BoardValidationException>(() => sync.SyncNowAsync("p", "b", ct));
        await sync.SyncDueAsync(ct);
        Assert.Empty(await sharing.DiscoverAsync(ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => sharing.ImportAsync("p", Guid.NewGuid().ToString("D"), ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => sharing.SaveAsync("p", "b", null, new BoardSharingEmailRequest("a@b.test"), ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => sharing.RemoveAsync("p", "b", "invite", ct));
    }

    [Fact]
    public async Task UploadsReportPausedWithoutSendingOrRecording()
    {
        ParserConfigs.SetApiKey(LocalKey);
        var handler = new StubHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var sessions = new SessionDataExportService(client, null!, NullLogger<SessionDataExportService>.Instance);
        var export = new DataExportService(client, null!, () => _directory, () => "Fixture");

        Assert.False(sessions.IsConfigured);
        var session = await sessions.ExportSessionAsync(Guid.NewGuid().ToString("D"), TestContext.Current.CancellationToken);
        Assert.Equal(SessionDataExportStatus.NotConfigured, session.Status);
        var data = await export.ExportAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DataExportStatus.NotConfigured, data.Status);
        Assert.Contains("local Front", DataExportRoutes.ToResponse(data).Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RegistrationPausesPublishersAndBlocksProductionInEveryClient()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, ["--web"], "http://127.0.0.1:12345");

        foreach (var job in new[] { typeof(TokenSavingsPublishJob), typeof(SessionDataDrainJob), typeof(CompleteBackupJob), typeof(BoardRemoteLaunchHostedService) })
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == job);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DataRetentionJob));

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<LocalFrontAccountKeyStore>(provider.GetRequiredService<IRemoteAccountKeyStore>());
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[] { "", BoardSyncHttpClient.HttpClientName, TokenSavingsPublishJob.HttpClientName, "signing-key-registration" })
        {
            using var client = factory.CreateClient(name);
            var blocked = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync("https://viberails.ai/api/v1/health", TestContext.Current.CancellationToken));
            Assert.Contains("Local Front mode blocked", blocked.Message);
        }
    }

    [Fact]
    public void NormalRegistrationStillHostsThePublishers()
    {
        using var normal = LocalFrontMode.UseForTests(LocalFrontState.Off);
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, ["--web"], "http://127.0.0.1:12345");

        foreach (var job in new[] { typeof(TokenSavingsPublishJob), typeof(SessionDataDrainJob), typeof(CompleteBackupJob) })
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == job);
        Assert.Contains(services, d => d.ServiceType == typeof(IRemoteAccountKeyStore) && d.ImplementationType == typeof(ApiKeyStore));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class NullRelay : IRemoteHttpRelayClient
    {
        public void Reset() { }
        public Task<HttpRelayResponse> SendAsync(HttpRelayRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
