using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Environments;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Resolving an Initial Message has side effects: a {{step:&lt;id&gt;}} reference runs a shell command.
/// The route's <c>HasActiveSession</c> check is a courtesy read, not a reservation, so resolving
/// before <c>StartSessionAsync</c> let two simultaneous requests both execute those commands even
/// though only one of them could go on to own the single terminal slot. The route now hands down
/// a delegate and the session service invokes it inside its lifecycle gate.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class TerminalRoutesPromptDeferralTests : IDisposable
{
    private readonly string originalRoot = VibeRails.Utils.ParserConfigs.GetRootPath();
    private readonly bool originalInGit = VibeRails.Utils.ParserConfigs.GetIsInGit();
    private readonly string? originalBranch = VibeRails.Utils.ParserConfigs.GetGitBranch();
    private readonly string? originalRemote = VibeRails.Utils.ParserConfigs.GetGitRemoteUrl();

    public TerminalRoutesPromptDeferralTests() => VibeRails.Utils.ParserConfigs.SetGitState(AppContext.BaseDirectory, true);
    public void Dispose() => VibeRails.Utils.ParserConfigs.SetGitState(originalRoot, originalInGit, originalBranch, originalRemote);
    private const string Prompt = "Deploy notes: {{step:2a1f0c4e-0000-4000-8000-000000000001}}";
    // One client per class: a client per test leaks TCP ports across the suite.
    private static readonly HttpClient SharedClient = new();

    [Theory]
    [InlineData("edited recap")]
    [InlineData(null)]
    public async Task ResumeAppendsBoardContextAfterEditedOrGeneratedRecap(string? editedRecap)
    {
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            var resume = Mock.Get(app.Services.GetRequiredService<ISessionResumeService>());
            var recap = editedRecap ?? "generated recap";
            resume.Setup(s => s.GetResumeSummaryAsync("source", It.IsAny<CancellationToken>())).ReturnsAsync(recap);
            resume.Setup(s => s.AppendBoardContextAsync("source", recap, It.IsAny<CancellationToken>()))
                .ReturnsAsync(recap + "\nBoard: VB-ABCDE-1 {{step:inert}}");
            terminal.Setup(t => t.StartSessionAsync(LLM.Claude, It.IsAny<string>(), null, It.IsAny<string[]>(),
                    It.IsAny<string>(), false, null, recap + "\nBoard: VB-ABCDE-1 {{step:inert}}", false)).ReturnsAsync(true);
            var client = SharedClient;
            using var response = await client.PostAsJsonAsync(new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(Cli: "Claude", ResumeSessionId: "source", ResumeSummary: editedRecap), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            terminal.VerifyAll();
            resume.Verify(s => s.AppendBoardContextAsync("source", recap, It.IsAny<CancellationToken>()), Times.Once);
            resume.Verify(s => s.GetResumeSummaryAsync("source", It.IsAny<CancellationToken>()), editedRecap is null ? Times.Once() : Times.Never());
            Assert.Empty(placeholders.Invocations);
        }
        finally { await StopAsync(app); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheAuthenticatedParentCarrierGrantsBoardTools(bool authorizeBoardTools)
    {
        var (app, terminal, _) = await StartAppAsync();
        try
        {
            terminal.Setup(item => item.StartSessionAsync(
                    LLM.Claude, It.IsAny<string>(), null, It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), authorizeBoardTools))
                .ReturnsAsync(true);
            var client = SharedClient;
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"));
            message.Content = new StringContent("{\"cli\":\"Claude\",\"authorizeBoardTools\":true}", System.Text.Encoding.UTF8, "application/json");
            if (authorizeBoardTools) message.Headers.Add("X-VibeRails-Board-Authorization", "true");
            using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            terminal.VerifyAll();
        }
        finally { await StopAsync(app); }
    }

    [Fact]
    public async Task BaseOptionsBecomeDiscreteArguments_AndACodexModeIsDroppedNotTyped()
    {
        var (app, terminal, _) = await StartAppAsync();
        try
        {
            // "plan" survives no further than BaseLlmOptionsBuilder: it becomes no argument, and
            // there is no longer any path that would type /plan into the running TUI instead.
            terminal.Setup(item => item.StartSessionAsync(
                    LLM.Codex, It.IsAny<string>(), null,
                    It.Is<string[]>(args => args.SequenceEqual(new[] { "--model", "gpt-5.5", "-c", "model_reasoning_effort=xhigh" })),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), false))
                .ReturnsAsync(true);
            var client = SharedClient;
            using var response = await client.PostAsJsonAsync(new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(WorkingDirectory: Path.GetTempPath(), Cli: "Codex", InitialPrompt: "card",
                    BaseLlmOptions: new("gpt-5.5", "max", "plan")), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            terminal.VerifyAll();
        }
        finally { await StopAsync(app); }
    }

    [Fact]
    public async Task SavedEnvironmentRejectsBaseOverridesBeforeLaunching()
    {
        var (app, terminal, _) = await StartAppAsync();
        try
        {
            var client = SharedClient;
            using var response = await client.PostAsJsonAsync(new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(WorkingDirectory: Path.GetTempPath(), Cli: "Codex", EnvironmentName: "saved",
                    BaseLlmOptions: new("gpt-5.5")), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(terminal.Invocations, invocation => invocation.Method.Name == nameof(ITerminalSessionService.StartSessionAsync));
        }
        finally { await StopAsync(app); }
    }

    [Fact]
    public async Task ThePromptIsNotResolvedBeforeTheSessionServiceIsCalled()
    {
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            Func<Task<ResolvedPrompt>>? captured = null;
            var resolvedBeforeTheCall = true;
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Callback((LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<ResolvedPrompt>>? resolve, string _, bool _, string? _, bool _) =>
                {
                    // Sampled at the moment the slot is claimed, not after the response: this is
                    // the window a losing concurrent request would have had to run the command in.
                    resolvedBeforeTheCall = placeholders.Invocations.Count > 0;
                    captured = resolve;
                })
                .ReturnsAsync(true);

            using var response = await PostStartAsync(app);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(resolvedBeforeTheCall, "The prompt was resolved before the session slot was claimed.");
            Assert.NotNull(captured);

            // Deferred, not dropped — the delegate still produces the prompt when the winner runs it.
            Assert.Equal("Deploy notes: ok", (await captured()).Prompt);
            placeholders.Verify(
                item => item.ResolveLaunchAsync(Prompt, It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task LosingTheRaceForTheSlot_RunsNoReferencedCommandsAtAll()
    {
        // The concurrency bug in one test: the session service declines because another request
        // already owns the terminal, so it never invokes the delegate and nothing is executed.
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(false);

            using var response = await PostStartAsync(app);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            placeholders.Verify(
                item => item.ResolveLaunchAsync(
                    It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task AnAlreadyActiveSession_IsRejectedWithoutResolvingAnything()
    {
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            terminal.SetupGet(item => item.HasActiveSession).Returns(true);

            using var response = await PostStartAsync(app);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            placeholders.Verify(
                item => item.ResolveLaunchAsync(
                    It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task NoInitialMessage_HandsDownNoResolverAtAll()
    {
        // A null delegate rather than one that resolves "" — the session service uses null to skip
        // the whole step, and an empty-string round trip would be a pointless service call.
        var (app, terminal, _) = await StartAppAsync();
        try
        {
            Func<Task<ResolvedPrompt>>? captured = () => Task.FromResult(new ResolvedPrompt(null, Path.GetTempPath()));
            string? capturedBoard = null;
            bool? capturedIncluded = null;
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), It.IsAny<bool>(), "selected-board", true))
                .Callback((LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<ResolvedPrompt>>? resolve, string _, bool _, string? board, bool included) =>
                {
                    captured = resolve;
                    capturedBoard = board;
                    capturedIncluded = included;
                })
                .ReturnsAsync(true);

            var client = SharedClient;
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(WorkingDirectory: Path.GetTempPath(), Cli: "Claude", ContextBoardId: "selected-board", ContextIncluded: true),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(captured);
            Assert.Equal("selected-board", capturedBoard);
            // A card prompt's "context already included" reaches the child, so it adds no second block.
            Assert.True(capturedIncluded);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Fact]
    public async Task AnOverlongResolvedPrompt_SurfacesItsOwnMessage()
    {
        // The throw now happens inside StartSessionAsync rather than in the route body, so the
        // handler's catch has to still recognize it instead of flattening it into the generic
        // "Failed to start terminal session" text.
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            placeholders
                .Setup(item => item.ResolveLaunchAsync(
                    It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(PromptTooLongException.ForResolvedPrompt(41_000, 30_000));
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(async (LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<ResolvedPrompt>>? resolve, string _, bool _, string? _, bool _) =>
                {
                    // Awaited, not observed: the real service lets the throw escape the gate so
                    // the route can answer with the message instead of starting a session.
                    await resolve!();
                    return true;
                });

            using var response = await PostStartAsync(app);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(error);
            Assert.Contains("30,000", error.Error, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(app);
        }
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(17, true, false)]
    [InlineData(17, false, true)]
    public async Task SavedEnvironmentsRequireAnIdAndProjectVisibility(int? environmentId, bool foreign, bool allowed)
    {
        var (app, terminal, _) = await StartAppAsync();
        try
        {
            var repository = Mock.Get(app.Services.GetRequiredService<IRepository>());
            repository.Setup(r => r.GetEnvironmentByIdAsync(17, It.IsAny<CancellationToken>())).ReturnsAsync(new LLM_Environment
            {
                Id = 17, LLM = LLM.Claude, CustomName = "canonical-name",
                ProjectPath = foreign ? Path.Combine(Path.GetTempPath(), "foreign-project-" + Guid.NewGuid().ToString("N")) : AppContext.BaseDirectory
            });
            terminal.Setup(t => t.StartSessionAsync(LLM.Claude, It.IsAny<string>(), "canonical-name", It.IsAny<string[]>(),
                It.IsAny<string>(), false, It.IsAny<Func<Task<ResolvedPrompt>>>(), It.IsAny<string>(), false)).ReturnsAsync(true);
            using var response = await SharedClient.PostAsJsonAsync(new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(Cli: "Claude", EnvironmentName: "stale-name", EnvironmentId: environmentId), TestContext.Current.CancellationToken);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            repository.Verify(r => r.GetEnvironmentByNameAndLlmAsync(It.IsAny<string>(), It.IsAny<LLM>(), It.IsAny<CancellationToken>()), Times.Never);
            if (!allowed) Assert.DoesNotContain(terminal.Invocations, i => i.Method.Name == nameof(ITerminalSessionService.StartSessionAsync));
        }
        finally { await StopAsync(app); }
    }

    private static Task<HttpResponseMessage> PostStartAsync(WebApplication app)
    {
        var client = SharedClient;
        return client.PostAsJsonAsync(
            new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
            new StartTerminalRequest(
                WorkingDirectory: Path.GetTempPath(),
                Cli: "Claude",
                InitialPrompt: Prompt),
            TestContext.Current.CancellationToken);
    }

    private static async Task<(
        WebApplication App,
        Mock<ITerminalSessionService> Terminal,
        Mock<IPromptPlaceholderService> Placeholders)> StartAppAsync()
    {
        var terminal = new Mock<ITerminalSessionService>();
        var placeholders = new Mock<IPromptPlaceholderService>();
        placeholders
            .Setup(item => item.ResolveLaunchAsync(
                It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedPrompt("Deploy notes: ok", Path.GetTempPath()));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(terminal.Object);
        builder.Services.AddSingleton(placeholders.Object);
        builder.Services.AddSingleton<ILlmParser, LlmParser>();
        builder.Services.AddSingleton(new Mock<IRepository>().Object);
        builder.Services.AddSingleton(new Mock<ISessionResumeService>().Object);
        builder.Services.AddSingleton(new VibeRails.ProcessRole(false, true));

        var app = builder.Build();
        TerminalRoutes.Map(app, Path.GetTempPath());
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, terminal, placeholders);
    }

    private static async Task StopAsync(WebApplication app)
    {
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
    }
}
