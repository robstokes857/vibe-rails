using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using Xunit;

namespace Tests;

public class LlmCliEnvironmentServiceTests
{
    [Fact]
    public async Task DeleteEnvironmentAsync_DeletesStoredPathRecursively()
    {
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var environment = new LLM_Environment
        {
            CustomName = "test",
            Path = @"C:\tmp\envs\test"
        };

        fileService.Setup(x => x.DirectoryExists(environment.Path)).Returns(true);

        await service.DeleteEnvironmentAsync(environment, CancellationToken.None);

        fileService.Verify(x => x.DeleteDirectory(environment.Path, true), Times.Once);
    }

    [Fact]
    public async Task DeleteEnvironmentAsync_FallsBackToConfiguredEnvironmentRoot()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var configuredEnvRoot = @"C:\tmp\envs";
        var expectedPath = Path.Combine(configuredEnvRoot, "test");
        var environment = new LLM_Environment
        {
            CustomName = "test",
            Path = ""
        };

        ParserConfigs.SetEnvPath(configuredEnvRoot);
        fileService.Setup(x => x.DirectoryExists(expectedPath)).Returns(true);

        try
        {
            await service.DeleteEnvironmentAsync(environment, CancellationToken.None);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }

        fileService.Verify(x => x.DeleteDirectory(expectedPath, true), Times.Once);
    }

    [Fact]
    public async Task DeleteEnvironmentAsync_DoesNothingWhenDirectoryMissing()
    {
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var environment = new LLM_Environment
        {
            CustomName = "test",
            Path = @"C:\tmp\envs\test"
        };

        fileService.Setup(x => x.DirectoryExists(environment.Path)).Returns(false);

        await service.DeleteEnvironmentAsync(environment, CancellationToken.None);

        fileService.Verify(x => x.DeleteDirectory(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    private static LlmCliEnvironmentService CreateService(IFileService fileService)
    {
        var dbService = Mock.Of<IDbService>();
        return new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(dbService, fileService),
            new CodexLlmCliEnvironment(dbService, fileService),
            new GeminiLlmCliEnvironment(dbService, fileService),
            new CopilotLlmCliEnvironment(dbService, fileService),
            dbService,
            fileService);
    }
}
