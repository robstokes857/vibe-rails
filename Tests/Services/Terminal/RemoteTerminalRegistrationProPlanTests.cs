using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Services.Integrations;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Remote terminals behind the Pro plan: a session is not offered to the relay while the plan is
/// known not to include them, and the relay's typed refusal of a registration is noted so later
/// terminals skip it. The local terminal is never involved in either.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class RemoteTerminalRegistrationProPlanTests : IAsyncDisposable
{
    private const string Key = "synthetic-registration-key";
    private const string Session = "3648c893-6634-42b3-8692-8988da904090";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _originalUrl = ParserConfigs.GetFrontendUrl();
    private readonly string _originalKey = ParserConfigs.GetApiKey();
    private readonly string _originalState = ParserConfigs.GetStatePath();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "terminal-registration-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _relay;
    private int _registrations;

    [Theory]
    // header and body, the body code alone, the header alone
    [InlineData(true, """{"error":"REMOTE PROSE","code":"pro_required","feature":"remote_terminals","payer":"account","upgradePath":"/Billing?feature=remote-terminals"}""")]
    [InlineData(false, """{"code":"pro_required","feature":"remote_terminals","payer":"account"}""")]
    [InlineData(true, "")]
    public async Task ARegistrationThePlanRefusesIsNoted_SoLaterTerminalsSkipIt(bool header, string body)
    {
        await StartRelayAsync(403, header, body);
        var entitlements = new FakeAccountEntitlements();
        var remote = Remote(entitlements);

        Assert.False(await remote.RegisterTerminalAsync(Session, "codex", "/project", null, ct: Ct));

        Assert.Equal([ProFeatures.RemoteTerminals], entitlements.Noted);
        Assert.Equal(1, _registrations);

        // The next terminal asks the plan first and sends nothing.
        var relayRegistration = new Mock<IRemoteStateService>(MockBehavior.Strict);
        using var state = State(relayRegistration.Object, entitlements);
        await state.RegisterRemoteSessionAsync(Session, "codex", "/project", null);
        relayRegistration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnyOtherRefusalIsNotAPlanDenial_AndAnAcceptedRegistrationStillSucceeds()
    {
        await StartRelayAsync(403, header: false, """{"code":"capability_required"}""");
        var entitlements = new FakeAccountEntitlements();
        Assert.False(await Remote(entitlements).RegisterTerminalAsync(Session, "codex", "/project", null, ct: Ct));
        Assert.Empty(entitlements.Noted);

        await _relay!.StopAsync(CancellationToken.None);
        await _relay.DisposeAsync();
        await StartRelayAsync(200, header: false, "{}");
        Assert.True(await Remote(entitlements).RegisterTerminalAsync(Session, "codex", "/project", null, ct: Ct));
        Assert.Empty(entitlements.Noted);
    }

    [Fact]
    public async Task ASessionIsOfferedToTheRelayOnlyWhileThePlanAllowsRemoteTerminals()
    {
        var entitlements = new FakeAccountEntitlements();
        var relayRegistration = new Mock<IRemoteStateService>();
        relayRegistration.Setup(r => r.RegisterTerminalAsync(Session, "codex", "/project", "env", null, null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var state = State(relayRegistration.Object, entitlements);

        entitlements.Off.Add(ProFeatures.RemoteTerminals);
        await state.RegisterRemoteSessionAsync(Session, "codex", "/project", "env");
        relayRegistration.Verify(r => r.RegisterTerminalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // A reconnect cannot register what creation did not.
        Assert.False(await state.ReregisterRemoteSessionAsync(Session, Ct));

        entitlements.Off.Clear(); // Pro again, or a plan that could not be read
        await state.RegisterRemoteSessionAsync(Session, "codex", "/project", "env");
        relayRegistration.Verify(r => r.RegisterTerminalAsync(Session, "codex", "/project", "env", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private RemoteStateService Remote(FakeAccountEntitlements entitlements) =>
        new(new HttpClient(), new ConfigurationBuilder().Build(), entitlements);

    private static TerminalStateService State(IRemoteStateService remote, FakeAccountEntitlements entitlements) =>
        new(Mock.Of<IRepository>(), Mock.Of<IGitService>(), remote, Mock.Of<ITerminalIoObserverService>(), entitlements: entitlements);

    private async Task StartRelayAsync(int status, bool header, string body)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _relay = builder.Build();
        // The account safety status is read first, exactly as before; it allows remote work here.
        _relay.MapGet("/api/v1/terminal/remote-access", () => Results.Text("""
            {"accountId":1,"generation":"0","remoteWork":{"state":"enabled","unlockUtc":null},"uploadsBlocked":false}
            """, "application/json"));
        _relay.MapPost("/api/v1/terminal", async context =>
        {
            Interlocked.Increment(ref _registrations);
            Assert.Equal(Key, context.Request.Headers["X-Api-Key"].ToString());
            if (header) context.Response.Headers["X-VibeRails-Denial"] = "pro_required";
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        });
        await _relay.StartAsync(Ct);
        Directory.CreateDirectory(_root);
        ParserConfigs.SetStatePath(Path.Combine(_root, "state.db"));
        ParserConfigs.SetFrontendUrl(_relay.Urls.First());
        ParserConfigs.SetApiKey(Key);
    }

    public async ValueTask DisposeAsync()
    {
        ParserConfigs.SetFrontendUrl(_originalUrl);
        ParserConfigs.SetApiKey(_originalKey);
        ParserConfigs.SetStatePath(_originalState);
        if (_relay is not null)
        {
            await _relay.StopAsync(CancellationToken.None);
            await _relay.DisposeAsync();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
