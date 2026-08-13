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
public sealed class TerminalRoutesPromptDeferralTests
{
    private const string Prompt = "Deploy notes: {{step:2a1f0c4e-0000-4000-8000-000000000001}}";

    [Fact]
    public async Task ThePromptIsNotResolvedBeforeTheSessionServiceIsCalled()
    {
        var (app, terminal, placeholders) = await StartAppAsync();
        try
        {
            Func<Task<string?>>? captured = null;
            var resolvedBeforeTheCall = true;
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<string?>>>(), It.IsAny<string>()))
                .Callback((LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<string?>>? resolve, string _) =>
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
            Assert.Equal("Deploy notes: ok", await captured());
            placeholders.Verify(
                item => item.ResolveAsync(Prompt, It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()),
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
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<string?>>>(), It.IsAny<string>()))
                .ReturnsAsync(false);

            using var response = await PostStartAsync(app);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            placeholders.Verify(
                item => item.ResolveAsync(
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
                item => item.ResolveAsync(
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
            Func<Task<string?>>? captured = () => Task.FromResult<string?>(null);
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<string?>>>(), It.IsAny<string>()))
                .Callback((LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<string?>>? resolve, string _) =>
                    captured = resolve)
                .ReturnsAsync(true);

            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/start"),
                new StartTerminalRequest(WorkingDirectory: Path.GetTempPath(), Cli: "Claude"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(captured);
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
                .Setup(item => item.ResolveAsync(
                    It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(PromptTooLongException.ForResolvedPrompt(41_000, 30_000));
            terminal
                .Setup(item => item.StartSessionAsync(
                    It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Func<Task<string?>>>(), It.IsAny<string>()))
                .Returns(async (LLM _, string _, string? _, string[]? _, string? _, bool _, Func<Task<string?>>? resolve, string _) =>
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

    private static Task<HttpResponseMessage> PostStartAsync(WebApplication app)
    {
        var client = new HttpClient();
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
            .Setup(item => item.ResolveAsync(
                It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Deploy notes: ok");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(terminal.Object);
        builder.Services.AddSingleton(placeholders.Object);
        builder.Services.AddSingleton<ILlmParser, LlmParser>();
        builder.Services.AddSingleton(new Mock<IRepository>().Object);
        builder.Services.AddSingleton(new Mock<ISessionResumeService>().Object);

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
