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
using Xunit;

namespace Tests.Routes;

/// <summary>
/// An environment whose name parses as a built-in CLI can never be launched: every launch path
/// resolves <c>--env &lt;value&gt;</c> as a CLI first and only falls through to a database lookup when
/// that fails. Such a name produces a base-CLI session carrying none of the environment's
/// arguments, credentials directory, or Initial Message — so creation is refused up front.
/// </summary>
public sealed class EnvironmentRoutesNameCollisionTests
{
    [Theory]
    [InlineData("Claude")]
    [InlineData("codex")]
    [InlineData("COPILOT")]
    [InlineData("  claude  ")]  // the name is trimmed before it is stored, so it has to be trimmed before it is checked
    [InlineData("3")]           // Enum.TryParse accepts the underlying numbers, so digits shadow a CLI too
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
    public async Task CreateEnvironment_RefusesThePseudoCliName_ViaTheCharacterRulesThatRunFirst(string name)
    {
        // Hyphenated pseudo-CLI wire names also resolve to a CLI, but their period is already
        // outside the character set a name may use (it becomes a directory under
        // ~/.vibe_rails/envs). Asserted explicitly so that relaxing those character rules later
        // cannot quietly make this name creatable.
        var (app, repository) = await StartAppAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                new CreateEnvironmentRequest(name, "Claude"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    [Fact]
    public async Task CreateEnvironment_AllowsANameThatMerelyContainsACliName()
    {
        // The check is an exact parse, not a substring match — "claude-nightly" is unambiguous
        // to every launch path and must stay creatable.
        var (app, _) = await StartAppAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsJsonAsync(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                new CreateEnvironmentRequest("claude-nightly", "Claude"),
                TestContext.Current.CancellationToken);

            var error = response.StatusCode == HttpStatusCode.BadRequest
                ? (await response.Content.ReadFromJsonAsync<ErrorResponse>(TestContext.Current.CancellationToken))?.Error
                : null;

            Assert.DoesNotContain("built-in CLI", error ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private static async Task<(WebApplication App, Mock<IRepository> Repository)> StartAppAsync()
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
        builder.Services.AddSingleton(new LlmCliEnvironmentService(
            new Mock<IClaudeLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<ICodexLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IAntigravityLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<ICopilotLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IOpencodeLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IFileService>(MockBehavior.Strict).Object));

        var app = builder.Build();
        EnvironmentRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, repository);
    }
}
