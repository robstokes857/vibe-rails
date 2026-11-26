using System.Text.Json;
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
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-171: the relay registration names the console host of a terminal on the bundled Windows
/// console host, where this desktop answers terminal queries. The hosted viewer then answers
/// none; a second answer is typed into the program. Every other terminal's registration is
/// unchanged, and the value has to be on the first registration and on every later one.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class RemoteTerminalRegistrationConsoleHostTests : IAsyncDisposable
{
    private const string Key = "synthetic-registration-key";
    private const string Session = "3648c893-6634-42b3-8692-8988da904090";
    // What a desktop sent before the member existed, for the arguments used below.
    private const string RegistrationBefore =
        """{"SessionId":"3648c893-6634-42b3-8692-8988da904090","Cli":"codex","WorkingDirectory":"/project","EnvironmentName":"env","Title":null,"HostUrl":"https://viberails.ai"}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _originalUrl = ParserConfigs.GetFrontendUrl();
    private readonly string _originalKey = ParserConfigs.GetApiKey();
    private readonly string _originalState = ParserConfigs.GetStatePath();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "terminal-console-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _bodies = [];
    private WebApplication? _relay;

    // ---- What goes over the wire -------------------------------------------------------------

    [Fact]
    public async Task ATerminalOnAnyOtherHostRegistersWithTheSameBytesAsBefore()
    {
        await StartRelayAsync();
        var remote = new RemoteStateService(new HttpClient(), new ConfigurationBuilder().Build());

        Assert.True(await remote.RegisterTerminalAsync(Session, "codex", "/project", "env", ct: Ct));
        Assert.True(await remote.RegisterTerminalAsync(Session, "codex", "/project", "env", consoleHost: null, ct: Ct));

        Assert.Equal([RegistrationBefore, RegistrationBefore], _bodies);
        // The same record without the member, written by the same serializer defaults.
        Assert.Equal(JsonSerializer.Serialize(new RegistrationBeforeConsoleHost(Session, "codex", "/project", "env", null, "https://viberails.ai")),
            _bodies[0]);
    }

    [Fact]
    public async Task ABundledHostTerminalSaysSoInItsRegistration()
    {
        await StartRelayAsync();
        var remote = new RemoteStateService(new HttpClient(), new ConfigurationBuilder().Build());

        Assert.True(await remote.RegisterTerminalAsync(Session, "codex", "/project", "env",
            consoleHost: TerminalSessionService.BundledConsoleHostWireName, ct: Ct));

        // One member more, last, and nothing else different.
        Assert.Equal(RegistrationBefore[..^1] + ",\"ConsoleHost\":\"bundled\"}", Assert.Single(_bodies));
    }

    // ---- First registration and every later one ------------------------------------------------

    [Theory]
    [InlineData("bundled")]
    [InlineData(null)]
    public async Task TheFirstRegistrationAndEveryReRegistrationCarryTheSameConsoleHost(string? consoleHost)
    {
        // The relay replaces the whole row on every registration. One sent without the value
        // after a reconnect would give a bundled-host terminal a viewer that answers queries.
        var registered = new List<string?>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new Mock<IRemoteStateService>();
        relay.Setup(r => r.RegisterTerminalAsync(It.IsAny<string>(), "codex", "/project", "env", null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(call =>
            {
                lock (registered) registered.Add((string?)call.Arguments[5]);
                first.TrySetResult();
            }))
            .ReturnsAsync(true);
        using var state = new TerminalStateService(Mock.Of<IRepository>(), Mock.Of<IGitService>(), relay.Object,
            Mock.Of<ITerminalIoObserverService>(), entitlements: new FakeAccountEntitlements(), isRemoteEnabled: () => true);

        var sessionId = await state.CreateSessionAsync("codex", "/project", "env", ct: Ct, consoleHost: consoleHost);
        try
        {
            // Sent while the session is created, before any PTY exists, and not awaited by it.
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.True(await state.ReregisterRemoteSessionAsync(sessionId, Ct));
            Assert.True(await state.ReregisterRemoteSessionAsync(sessionId, Ct));

            lock (registered) Assert.Equal([consoleHost, consoleHost, consoleHost], registered);
        }
        finally
        {
            await state.CompleteSessionAsync(sessionId, 0);
        }
    }

    [Fact]
    public async Task ASessionThatIsNotRemoteRegistersNothingWhateverItsConsoleHost()
    {
        var relay = new Mock<IRemoteStateService>(MockBehavior.Strict);
        relay.Setup(r => r.DeregisterTerminalAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        using var state = new TerminalStateService(Mock.Of<IRepository>(), Mock.Of<IGitService>(), relay.Object,
            Mock.Of<ITerminalIoObserverService>(), entitlements: new FakeAccountEntitlements(), isRemoteEnabled: () => false);

        var sessionId = await state.CreateSessionAsync("codex", "/project", "env", ct: Ct, consoleHost: "bundled");
        try
        {
            Assert.False(await state.ReregisterRemoteSessionAsync(sessionId, Ct));
            relay.Verify(r => r.RegisterTerminalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await state.CompleteSessionAsync(sessionId, 0);
        }
    }

    // ---- Known before the PTY exists ---------------------------------------------------------

    [Theory]
    [InlineData(false, false)]  // The default, a terminal tab: the bundled host.
    [InlineData(true, false)]   // The built-in host chosen in App settings.
    [InlineData(false, true)]   // A native console session stays on the built-in host.
    public async Task TheRunnerNamesTheConsoleHostWhenItCreatesTheSession(bool builtInChosen, bool nativeConsole)
    {
        // Creating the session is what sends the first registration, so the host has to be
        // resolved by then. The runner stops here: no PTY is started.
        var named = "never asked";
        var state = new Mock<ITerminalStateService>();
        state.Setup(s => s.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Callback(new InvocationAction(call => named = (string?)call.Arguments[9]))
            .ThrowsAsync(new SessionWasAskedFor());
        var runner = new TerminalRunner(state.Object, Mock.Of<ICommandService>(), Mock.Of<ILocalToolApiContext>(),
            Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), Mock.Of<IRepository>(),
            Mock.Of<IEnvironmentStepRunner>(), Mock.Of<IAppEventBus>(),
            loadSettings: () => new Settings { BuiltInConsoleHost = builtInChosen });

        await Assert.ThrowsAsync<SessionWasAskedFor>(() => runner.CreateSessionAsync(
            LLM.Codex, Path.GetTempPath(), null, null, Ct, isNativeCli: nativeConsole));

        var expectBundled = !builtInChosen && !nativeConsole && BundledConsoleHostTests.IsWindowsX64;
        Assert.Equal(expectBundled ? "bundled" : null, named);
        // The name the desktop's own start, status and tab-list responses use.
        Assert.Equal("bundled", TerminalSessionService.BundledConsoleHostWireName);
    }

    private sealed class SessionWasAskedFor : Exception;

    private sealed record RegistrationBeforeConsoleHost(
        string SessionId, string Cli, string WorkingDirectory, string? EnvironmentName, string? Title, string HostUrl);

    private async Task StartRelayAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _relay = builder.Build();
        // The account safety status is read first, as for every registration; it allows remote work.
        _relay.MapGet("/api/v1/terminal/remote-access", () => Results.Text("""
            {"accountId":1,"generation":"0","remoteWork":{"state":"enabled","unlockUtc":null},"uploadsBlocked":false}
            """, "application/json"));
        _relay.MapPost("/api/v1/terminal", async context =>
        {
            Assert.Equal(Key, context.Request.Headers["X-Api-Key"].ToString());
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            lock (_bodies) _bodies.Add(body);
            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}");
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
