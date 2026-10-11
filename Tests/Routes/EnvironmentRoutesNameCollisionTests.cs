using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// An environment whose name parses as a built-in CLI can never be launched: every launch path
/// resolves <c>--env &lt;value&gt;</c> as a CLI first and only falls through to a database lookup when
/// that fails. Such a name produces a base-CLI session carrying none of the environment's
/// arguments, credentials directory, or Initial Message — so creation is refused up front.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class EnvironmentRoutesNameCollisionTests
{
    [Theory]
    [InlineData("Claude")]
    [InlineData("codex")]
    [InlineData("COPILOT")]
    [InlineData("  claude  ")]  // the name is trimmed before it is stored, so it has to be trimmed before it is checked
    [InlineData("3")]           // Enum.TryParse accepts the underlying numbers, so digits shadow a CLI too
    [InlineData("kimi-k3")]     // no dot, so the character rules pass it and the CLI check below is what refuses it
    [InlineData("grok")]
    public async Task CreateEnvironment_RefusesANameThatResolvesToACli(string name)
    {
        var (app, repository) = await StartAppAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                new CreateEnvironmentRequest(name, "Claude"),
                TestContext.Current.CancellationToken);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(error);
            Assert.Contains("built-in CLI", error.Error, StringComparison.Ordinal);
            // Rejected before anything is written: no row, and no per-environment credentials
            // directory created on disk under a name that could never be launched.
            repository.Verify(
                item => item.SaveEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("glm-5.2")]
    [InlineData("grok-4.6")]
    [InlineData("glm-5.3")]
    [InlineData("deepseek-v4-pro")]
    public async Task CreateEnvironment_RefusesThePseudoCliName(string name)
    {
        // Periods in a saved setup name are allowed, but an exact CLI name still shadows
        // environment lookup and must be rejected before provisioning.
        var (app, repository) = await StartAppAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                new CreateEnvironmentRequest(name, "Claude"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(TestContext.Current.CancellationToken);
            Assert.Contains("built-in CLI", error!.Error, StringComparison.Ordinal);
            repository.Verify(
                item => item.SaveEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("claude-nightly")]
    [InlineData("Claude Fable 5.1 X High")]
    public async Task CreateEnvironment_AllowsANameThatMerelyContainsACliName(string name)
    {
        // The check is an exact parse, not a substring match — "claude-nightly" is unambiguous
        // to every launch path and must stay creatable.
        var originalEnvPath = ParserConfigs.GetEnvPath();
        // Only path calculation is exercised: provisioning and persistence are mocked.
        ParserConfigs.SetEnvPath(Path.Combine(Path.GetTempPath(), "vb-env-name-tests"));
        var (app, repository) = await StartAppAsync(allowProvisioning: true);
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                new CreateEnvironmentRequest(name, "Claude"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<EnvironmentResponse>(TestContext.Current.CancellationToken);
            Assert.Equal(name, created!.Name);
            Assert.Equal("", created.CustomPrompt);
            repository.Verify(item => item.SaveEnvironmentAsync(
                It.Is<LLM_Environment>(environment => environment.CustomName == name), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    private static async Task<(WebApplication App, Mock<IRepository> Repository)> StartAppAsync(bool allowProvisioning = false)
    {
        var repository = new Mock<IRepository>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRepository>(repository.Object);
        builder.Services.AddSingleton<ILlmParser, LlmParser>();
        builder.Services.AddSingleton(new Mock<IJobStore>().Object);
        builder.Services.AddSingleton(new Mock<IRunWorkspaceService>(MockBehavior.Strict).Object);
        // Strict throughout: a rejected name must not reach environment provisioning, so any
        // call here is the failure the test is guarding against.
        var claude = new Mock<IClaudeLlmCliEnvironment>(MockBehavior.Strict);
        if (allowProvisioning)
            claude.Setup(item => item.SaveEnvironment(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        builder.Services.AddSingleton(new LlmCliEnvironmentService(
            claude.Object,
            new Mock<ICodexLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IAntigravityLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<ICopilotLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IOpencodeLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IGrokLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IFileService>(MockBehavior.Strict).Object));

        var app = builder.Build();
        EnvironmentRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, repository);
    }
}
