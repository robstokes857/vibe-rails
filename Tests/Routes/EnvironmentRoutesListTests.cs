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
using VibeRails.Services.Workspaces;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class EnvironmentRoutesListTests
{
    // The Agents page tells a project's own setups from the ones every project sees (saved before
    // project scoping, blank ProjectPath) by this field alone, so the list must carry it as stored:
    // the project for an owned row, null for a legacy one that the route still lists.
    [Fact]
    public async Task ListEnvironments_ReportsTheSavedProjectAndNullForLegacyRows()
    {
        var projectPath = ParserConfigs.GetRootPath();
        var own = new LLM_Environment
        {
            Id = 1, CustomName = "here", LLM = LLM.Codex, Path = "unused", CustomPrompt = "Review.", ProjectPath = projectPath
        };
        var legacy = new LLM_Environment
        {
            Id = 2, CustomName = "old helper", LLM = LLM.Claude, Path = "unused", CustomPrompt = "Help.", ProjectPath = null
        };
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.GetCustomEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LLM_Environment> { own, legacy });
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
            var byName = response.Environments.ToDictionary(item => item.Name);
            Assert.Equal(2, byName.Count);
            Assert.Equal(projectPath, byName["here"].ProjectPath);
            Assert.Null(byName["old helper"].ProjectPath);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
