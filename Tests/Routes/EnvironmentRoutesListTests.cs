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
using VibeRails.Services.LlmClis;
using VibeRails.Interfaces;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class EnvironmentRoutesListTests : IDisposable
{
    private readonly string _originalRoot = VibeRails.Utils.ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = VibeRails.Utils.ParserConfigs.GetIsInGit();
    private readonly string? _originalBranch = VibeRails.Utils.ParserConfigs.GetGitBranch();
    private readonly string? _originalRemote = VibeRails.Utils.ParserConfigs.GetGitRemoteUrl();

    public EnvironmentRoutesListTests() =>
        VibeRails.Utils.ParserConfigs.SetGitState(AppContext.BaseDirectory, isInGit: true);

    public void Dispose() =>
        VibeRails.Utils.ParserConfigs.SetGitState(_originalRoot, _originalIsInGit, _originalBranch, _originalRemote);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListEnvironments_ReturnsOnlyTheCurrentProjectsSetups(string? legacyProjectPath)
    {
        var projectPath = ParserConfigs.GetRootPath();
        var own = new LLM_Environment
        {
            Id = 1, CustomName = "here", LLM = LLM.Codex, Path = "unused", CustomPrompt = "Review.", ProjectPath = projectPath
        };
        var legacy = new LLM_Environment
        {
            Id = 2, CustomName = "old helper", LLM = LLM.Claude, Path = "unused", CustomPrompt = "Help.", ProjectPath = legacyProjectPath
        };
        var foreign = new LLM_Environment
        {
            Id = 3, CustomName = "elsewhere", LLM = LLM.Codex, Path = "unused", CustomPrompt = "Help.",
            ProjectPath = Path.Combine(projectPath, "another-repository")
        };
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.GetCustomEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LLM_Environment> { own, legacy, foreign });
        repository
            .Setup(item => item.FindEnvironmentByNameAsync(legacy.CustomName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(legacy);
        repository
            .Setup(item => item.FindEnvironmentByNameAsync(foreign.CustomName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(foreign);
        repository
            .Setup(item => item.GetSandboxesByProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Sandbox>());
        repository
            .Setup(item => item.GetStepsForEnvironmentsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, List<EnvironmentStep>>());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRepository>(repository.Object);
        builder.Services.AddSingleton(Mock.Of<IJobStore>());
        builder.Services.AddSingleton(Mock.Of<IRunWorkspaceService>());
        builder.Services.AddSingleton(new LlmCliEnvironmentService(
            new Mock<IClaudeLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<ICodexLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IAntigravityLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<ICopilotLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IOpencodeLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IGrokLlmCliEnvironment>(MockBehavior.Strict).Object,
            new Mock<IFileService>(MockBehavior.Strict).Object));

        await using var app = builder.Build();
        EnvironmentRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetFromJsonAsync<EnvironmentListResponse>(
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments"),
                TestContext.Current.CancellationToken);

            Assert.NotNull(response);
            var environment = Assert.Single(response.Environments);
            Assert.Equal("here", environment.Name);
            Assert.Equal(projectPath, environment.ProjectPath);
            repository.Verify(item => item.GetStepsForEnvironmentsAsync(
                It.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == own.Id),
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(legacyProjectPath, legacy.ProjectPath);

            foreach (var excluded in new[] { legacy, foreign })
            {
                var url = new Uri(new Uri(app.Urls.First()), $"/api/v1/environments/{Uri.EscapeDataString(excluded.CustomName)}");
                using var read = await client.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.NotFound, read.StatusCode);
                using var update = await client.PutAsJsonAsync(url, new { customPrompt = "Changed" }, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.NotFound, update.StatusCode);
                using var delete = await client.DeleteAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.NotFound, delete.StatusCode);
                Assert.Equal("Help.", excluded.CustomPrompt);
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
