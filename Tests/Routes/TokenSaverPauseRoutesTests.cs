using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Services.Terminal;
using TokenSaver;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Drives the agent-facing token-saver control endpoints through real Kestrel. The behaviour that
/// matters is the whole point of the feature: a pause reaches the process that does the
/// compressing, it expires on its own, and it is gated by the same session+tab contract as the
/// proxy it sits beside — a session token alone must not let anything turn compression off.
/// </summary>
public sealed class TokenSaverPauseRoutesTests
{
    private const string SessionToken = "test-session-token";
    private const string TabToken = "test-tab-token";

    // ONE client for the class: per-test HttpClients leave sockets in TIME_WAIT and enough of
    // those clogs the machine's ephemeral ports.
    private static readonly HttpClient SharedClient = new();

    private static readonly DateTimeOffset Start = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pause_SuppressesCompressionForTheProxyInThisProcess()
    {
        await using var host = await StartAsync();

        // Before: this process is compressing.
        Assert.True(host.Settings.GetSettings().ClaudeTokenSaverEnabled);

        var paused = await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);

        Assert.Equal(Start + TokenSaverPauseState.PauseWindow, DateTimeOffset.Parse(paused.PausedUntilUtc!));
        Assert.True(paused.SaverEnabled);
        // The proxy route reads this same snapshot per request, so this is the assertion that the
        // pause actually lands where compression happens.
        Assert.False(host.Settings.GetSettings().ClaudeTokenSaverEnabled);
        // The relay itself must stay up: 404-ing the CLI mid-conversation is not a pause.
        Assert.True(host.Settings.GetSettings().ClaudeLlmProxyEnabled);
    }

    [Fact]
    public async Task Pause_LapsesOnItsOwnWithoutAResumeCall()
    {
        await using var host = await StartAsync();
        await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);

        host.Clock.SetUtcNow(Start + TokenSaverPauseState.PauseWindow);

        // The safety property: nothing an agent does can leave compression off, and nothing has to
        // fire for it to come back.
        Assert.True(host.Settings.GetSettings().ClaudeTokenSaverEnabled);
        var status = await host.SendAsync(HttpMethod.Get, "status", TestContext.Current.CancellationToken);
        Assert.Null(status.PausedUntilUtc);
    }

    [Fact]
    public async Task Resume_RestoresCompressionImmediately()
    {
        await using var host = await StartAsync();
        await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);

        var resumed = await host.SendAsync(HttpMethod.Post, "resume", TestContext.Current.CancellationToken);

        Assert.Null(resumed.PausedUntilUtc);
        Assert.True(host.Settings.GetSettings().ClaudeTokenSaverEnabled);
    }

    [Fact]
    public async Task PauseAndResume_EachPublishOneEventForTheMeter()
    {
        await using var host = await StartAsync();

        await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);
        await host.SendAsync(HttpMethod.Get, "status", TestContext.Current.CancellationToken);
        await host.SendAsync(HttpMethod.Post, "resume", TestContext.Current.CancellationToken);

        // Two, not three: a status poll is read-only and must never repaint the meter.
        Assert.Equal(2, host.Events.Count);
        Assert.All(host.Events, e => Assert.Equal("token_saver_pause", e.Type));

        var pausedAt = host.Events[0].Payload.GetProperty("pausedUntilUtc").GetString();
        Assert.Equal(Start + TokenSaverPauseState.PauseWindow, DateTimeOffset.Parse(pausedAt!));
        // The browser runs its own countdown off this absolute instant, so a resume has to say
        // "no expiry" rather than simply stop talking.
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            host.Events[1].Payload.GetProperty("pausedUntilUtc").ValueKind);
    }

    [Fact]
    public async Task Resume_WhenNotPaused_PublishesNothing()
    {
        await using var host = await StartAsync();

        var resumed = await host.SendAsync(HttpMethod.Post, "resume", TestContext.Current.CancellationToken);

        Assert.Null(resumed.PausedUntilUtc);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Status_ReportsSaverSwitchedOff_WithoutConflatingItWithAPause()
    {
        await using var host = await StartAsync(saverConfiguredOn: false);

        var status = await host.SendAsync(HttpMethod.Get, "status", TestContext.Current.CancellationToken);

        Assert.False(status.SaverEnabled);
        Assert.Null(status.PausedUntilUtc);
    }

    [Fact]
    public async Task Pause_WithTheSaverSwitchedOff_StartsNoWindowAtAll()
    {
        await using var host = await StartAsync(saverConfiguredOn: false);

        var paused = await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);

        Assert.False(paused.SaverEnabled);
        Assert.Null(paused.PausedUntilUtc);
        // The response truthfully says nothing was paused and the meter hides its badge on that
        // answer — so a window started here would exist in no surface that could report it.
        Assert.False(host.Pause.IsPaused);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Pause_WithTheSaverSwitchedOff_LeavesCompressionRunningIfItIsSwitchedOnLater()
    {
        // The consequence of an invisible window: switching the saver on inside it would suppress
        // compression for the rest of the five minutes, with nothing on screen to explain why.
        await using var host = await StartAsync(saverConfiguredOn: false);
        await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);

        host.Settings.ConfiguredOn = true;

        Assert.True(host.Settings.GetSettings().ClaudeTokenSaverEnabled);
    }

    [Fact]
    public async Task Resume_StillClearsAWindowOpenedBeforeTheSaverWasSwitchedOff()
    {
        // The asymmetry that makes refusing to pause safe: pause is skipped when there is nothing
        // to pause, but resume always runs, so a window opened while the saver was on stays
        // clearable after it is switched off.
        await using var host = await StartAsync();
        await host.SendAsync(HttpMethod.Post, "pause", TestContext.Current.CancellationToken);
        host.Settings.ConfiguredOn = false;

        var resumed = await host.SendAsync(HttpMethod.Post, "resume", TestContext.Current.CancellationToken);

        Assert.Null(resumed.PausedUntilUtc);
        Assert.False(host.Pause.IsPaused);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("status")]
    public async Task MissingTabToken_IsRejected(string action)
    {
        // Session alone is deliberately not enough — this endpoint acts on a specific terminal.
        await using var host = await StartAsync();
        var method = action == "status" ? HttpMethod.Get : HttpMethod.Post;

        using var request = new HttpRequestMessage(method, host.Url(action));
        request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, SessionToken);

        using var response = await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.Pause.IsPaused);
    }

    [Fact]
    public async Task NoTokens_IsRejected()
    {
        await using var host = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, host.Url("pause"));
        using var response = await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.Pause.IsPaused);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("status")]
    public async Task UnauthenticatedCall_GetsAStatusCode_NotTheHtmlAuthPage(string action)
    {
        // CookieAuthMiddleware answers an unauthenticated browser request with a 403 and the
        // "auth required" HTML page. These endpoints are reached by an MCP tool, which surfaces
        // whatever comes back as its result verbatim — a page of markup there tells the agent
        // nothing it can act on, and reads as a malfunction rather than as "not authorised".
        // The middleware has to recognise the control prefix for that to hold, which is the wiring
        // this pins (CookieAuthMiddleware -> TokenSaverPauseRoutes.IsControlPath).
        await using var host = await StartAsync();
        var method = action == "status" ? HttpMethod.Get : HttpMethod.Post;

        using var request = new HttpRequestMessage(method, host.Url(action));
        using var response = await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.False(host.Pause.IsPaused);
    }

    private static async Task<TestHost> StartAsync(bool saverConfiguredOn = true)
    {
        // Port 0 = kernel-assigned: parallel Kestrel-hosted test classes race a find-then-rebind
        // port picker, so bind ephemeral and read the real address after start.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var authService = new Mock<IAuthService>(MockBehavior.Loose);
        authService.Setup(service => service.ValidateToken(SessionToken)).Returns(true);
        authService.Setup(service => service.ValidateTabToken(TabToken)).Returns(true);

        var clock = new ManualTimeProvider(Start);
        var pause = new TokenSaverPauseState(clock);
        var settings = new StubSettingsService(pause, saverConfiguredOn);
        var eventBus = new AppEventBus();
        var events = new List<AppEvent>();
        var subscription = eventBus.Subscribe(events.Add);

        builder.Services.AddSingleton<IAuthService>(authService.Object);
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new LlmProxyAuthGateAdapter(authService.Object));
        builder.Services.AddSingleton<ITokenSaverPauseState>(pause);
        builder.Services.AddSingleton<ITokenSaverConfiguration>(settings);
        builder.Services.AddSingleton<IAppEventBus>(eventBus);

        var app = builder.Build();
        // The real app's outermost gate, included so every test here runs the composed stack rather
        // than the route in isolation. It changes what an unauthenticated caller receives, and the
        // MCP tool on the other end can only report what it receives.
        app.UseMiddleware<CookieAuthMiddleware>();
        TokenSaverPauseRoutes.Map(app);
        await app.StartAsync();

        return new TestHost(app, clock, pause, settings, events, subscription);
    }

    private sealed class TestHost(
        WebApplication app,
        ManualTimeProvider clock,
        TokenSaverPauseState pause,
        StubSettingsService settings,
        List<AppEvent> events,
        IDisposable subscription) : IAsyncDisposable
    {
        public ManualTimeProvider Clock => clock;
        public TokenSaverPauseState Pause => pause;
        public StubSettingsService Settings => settings;
        public List<AppEvent> Events => events;

        public Uri Url(string action) => new(
            new Uri(app.Urls.First()),
            $"{TokenSaverPauseRoutes.ControlPathPrefix}/{action}");

        public async Task<TokenSaverPausePayload> SendAsync(
            HttpMethod method, string action, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, Url(action));
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, SessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.TabHeaderName, TabToken);

            using var response = await SharedClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TokenSaverPausePayload>(cancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            subscription.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Stands in for <see cref="LlmProxySettingsService"/> without touching the developer's real
    /// settings.json. It applies the pause the same way the real one does, which is what lets these
    /// tests assert that a pause actually reaches the snapshot the proxy routes read per request.
    /// </summary>
    internal sealed class StubSettingsService(ITokenSaverPauseState pause, bool configuredOn)
        : ILlmProxySettingsService, ITokenSaverConfiguration
    {
        /// <summary>Settable so a test can switch the saver on or off mid-flight, as the UI does.</summary>
        public bool ConfiguredOn { get; set; } = configuredOn;

        public LlmProxySettings GetSettings() => new(
            CodexLlmProxyEnabled: false,
            CodexLlmProxyMode: CodexLlmProxySettings.ModeSubscription,
            ClaudeLlmProxyEnabled: true,
            ClaudeTokenSaverEnabled: ConfiguredOn && !pause.IsPaused);

        public bool IsConfiguredOn() => ConfiguredOn;
    }
}
