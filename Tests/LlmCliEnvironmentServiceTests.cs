using Moq;
using Microsoft.Extensions.Configuration;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;
using Xunit;

namespace Tests;

// Serialized with the other classes that mutate the process-global ParserConfigs.EnvPath
// (CommandServiceTests etc.) so parallel collections can't clobber each other's env root mid-test.
[Collection("ProcessEnvIsolation")]
public class LlmCliEnvironmentServiceTests
{
    [Fact]
    public async Task CreateEnvironmentAsync_RejectsPathTraversalAtServiceBoundary()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-boundary-{Guid.NewGuid():N}");
        var codexEnvironment = new Mock<ICodexLlmCliEnvironment>();
        var service = new LlmCliEnvironmentService(
            Mock.Of<IClaudeLlmCliEnvironment>(),
            codexEnvironment.Object,
            Mock.Of<IAntigravityLlmCliEnvironment>(),
            Mock.Of<ICopilotLlmCliEnvironment>(),
            Mock.Of<IOpencodeLlmCliEnvironment>(),
            Mock.Of<IGrokLlmCliEnvironment>(),
            Mock.Of<IFileService>());
        var environment = new LLM_Environment
        {
            LLM = LLM.Codex,
            CustomName = "../outside"
        };

        Directory.CreateDirectory(configuredEnvRoot);
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateEnvironmentAsync(environment, TestContext.Current.CancellationToken));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
            Directory.Delete(configuredEnvRoot, recursive: true);
        }

        codexEnvironment.Verify(
            x => x.SaveEnvironment(
                It.IsAny<LLM_Environment>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void StageEnvironmentDirectoryForDeletion_FallsBackToConfiguredEnvironmentRoot()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-stage-{Guid.NewGuid():N}");
        var expectedPath = Path.GetFullPath(Path.Combine(configuredEnvRoot, "test"));
        var environment = new LLM_Environment
        {
            Id = 42,
            CustomName = "test",
            Path = ""
        };

        ParserConfigs.SetEnvPath(configuredEnvRoot);
        fileService.Setup(x => x.DirectoryExists(expectedPath)).Returns(true);

        LlmCliEnvironmentService.StagedEnvironmentDirectory stagedDirectory;
        try
        {
            stagedDirectory = service.StageEnvironmentDirectoryForDeletion(environment);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }

        Assert.Equal(expectedPath, stagedDirectory.OriginalPath);
        Assert.NotNull(stagedDirectory.TombstonePath);
        Assert.Equal(
            Path.GetDirectoryName(expectedPath),
            Path.GetDirectoryName(stagedDirectory.TombstonePath));
        Assert.StartsWith(".viberails-delete-42-", Path.GetFileName(stagedDirectory.TombstonePath));
        fileService.Verify(
            x => x.MoveDirectory(expectedPath, stagedDirectory.TombstonePath),
            Times.Once);
        fileService.Verify(
            x => x.DeleteDirectory(It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public void StageEnvironmentDirectoryForDeletion_DoesNothingWhenDirectoryMissing()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-missing-{Guid.NewGuid():N}");
        var environment = new LLM_Environment
        {
            CustomName = "test",
            Path = Path.Combine(configuredEnvRoot, "test")
        };

        fileService.Setup(x => x.DirectoryExists(environment.Path)).Returns(false);
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        LlmCliEnvironmentService.StagedEnvironmentDirectory stagedDirectory;
        try
        {
            stagedDirectory = service.StageEnvironmentDirectoryForDeletion(environment);
            service.FinalizeStagedEnvironmentDirectoryDeletion(stagedDirectory);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }

        Assert.Null(stagedDirectory.TombstonePath);
        fileService.Verify(
            x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        fileService.Verify(x => x.DeleteDirectory(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void StageEnvironmentDirectoryForDeletion_DoesNotEnumerateMissingParent()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-missing-parent-{Guid.NewGuid():N}");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));

        Assert.False(Directory.Exists(configuredEnvRoot));
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var stagedDirectory = service.StageEnvironmentDirectoryForDeletion(new LLM_Environment
            {
                Id = 50,
                CustomName = "missing",
                Path = Path.Combine(configuredEnvRoot, "missing")
            });

            Assert.Null(stagedDirectory.TombstonePath);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    [Fact]
    public void StageEnvironmentDirectoryForDeletion_LeavesStoredOutsideRootUntouched()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-containment-{Guid.NewGuid():N}");
        var configuredEnvRoot = Path.Combine(testRoot, "envs");
        var outsidePath = Path.Combine(testRoot, "outside");
        var outsideMarker = Path.Combine(outsidePath, "credentials.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));

        Directory.CreateDirectory(configuredEnvRoot);
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(outsideMarker, "do not touch");
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var stagedDirectory = service.StageEnvironmentDirectoryForDeletion(new LLM_Environment
            {
                Id = 52,
                CustomName = "review",
                Path = outsidePath
            });

            service.FinalizeStagedEnvironmentDirectoryDeletion(stagedDirectory);

            Assert.Null(stagedDirectory.OriginalPath);
            Assert.Null(stagedDirectory.TombstonePath);
            Assert.Equal("do not touch", File.ReadAllText(outsideMarker));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void StageEnvironmentDirectoryForDeletion_RejectsConcurrentDeleteTombstone()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-concurrent-{Guid.NewGuid():N}");
        var environmentPath = Path.Combine(configuredEnvRoot, "review");
        var tombstonePath = Path.Combine(
            configuredEnvRoot,
            ".viberails-delete-51-0123456789abcdef0123456789abcdef");
        var environment = new LLM_Environment
        {
            Id = 51,
            CustomName = "review",
            Path = environmentPath
        };

        fileService.Setup(x => x.DirectoryExists(environmentPath)).Returns(false);
        fileService.Setup(x => x.DirectoryExists(configuredEnvRoot)).Returns(true);
        fileService.Setup(x => x.EnumerateDirectories(
                configuredEnvRoot,
                ".viberails-delete-51-*",
                It.IsAny<EnumerationOptions>()))
            .Returns([tombstonePath]);
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            Assert.Throws<LlmCliEnvironmentService.EnvironmentDeletionInProgressException>(() =>
                service.StageEnvironmentDirectoryForDeletion(environment));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }

        fileService.Verify(
            x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public void FinalizeStagedEnvironmentDirectoryDeletion_DoesNotDeleteRecreatedOriginal()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-recreate-{Guid.NewGuid():N}");
        var environmentPath = Path.Combine(configuredEnvRoot, "review");
        var originalMarkerPath = Path.Combine(environmentPath, "original.txt");
        var recreatedMarkerPath = Path.Combine(environmentPath, "recreated.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));

        Directory.CreateDirectory(environmentPath);
        File.WriteAllText(originalMarkerPath, "old credentials");
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            // Stage the old directory, then simulate the guarded row deletion followed by a
            // concurrent recreation at the now-available original path.
            var stagedDirectory = service.StageEnvironmentDirectoryForDeletion(new LLM_Environment
            {
                Id = 73,
                CustomName = "review",
                Path = environmentPath
            });

            Assert.False(Directory.Exists(environmentPath));
            Assert.NotNull(stagedDirectory.TombstonePath);
            Assert.True(File.Exists(Path.Combine(stagedDirectory.TombstonePath, "original.txt")));

            Directory.CreateDirectory(environmentPath);
            File.WriteAllText(recreatedMarkerPath, "new credentials");

            service.FinalizeStagedEnvironmentDirectoryDeletion(stagedDirectory);

            Assert.True(Directory.Exists(environmentPath));
            Assert.True(File.Exists(recreatedMarkerPath));
            Assert.Equal("new credentials", File.ReadAllText(recreatedMarkerPath));
            Assert.False(Directory.Exists(stagedDirectory.TombstonePath));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
            if (Directory.Exists(configuredEnvRoot))
            {
                Directory.Delete(configuredEnvRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void RestoreStagedEnvironmentDirectory_ReturnsOriginalDirectoryOnRefusal()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-restore-{Guid.NewGuid():N}");
        var environmentPath = Path.Combine(configuredEnvRoot, "review");
        var markerPath = Path.Combine(environmentPath, "settings.json");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));

        Directory.CreateDirectory(environmentPath);
        File.WriteAllText(markerPath, "preserve me");
        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var stagedDirectory = service.StageEnvironmentDirectoryForDeletion(new LLM_Environment
            {
                Id = 74,
                CustomName = "review",
                Path = environmentPath
            });

            service.RestoreStagedEnvironmentDirectory(stagedDirectory);

            Assert.True(File.Exists(markerPath));
            Assert.Equal("preserve me", File.ReadAllText(markerPath));
            Assert.False(Directory.Exists(stagedDirectory.TombstonePath));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
            if (Directory.Exists(configuredEnvRoot))
            {
                Directory.Delete(configuredEnvRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GetEnvironmentVariables_OpenCodeUsesIsolatedXdgConfigRoot()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(Path.GetTempPath(), $"viberails-opencode-{Guid.NewGuid():N}");
        var expectedPath = Path.GetFullPath(Path.Combine(configuredEnvRoot, "review"));
        var service = CreateService(Mock.Of<IFileService>());

        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var variables = service.GetEnvironmentVariables("review", LLM.OpenCode);

            Assert.Equal(expectedPath, variables["XDG_CONFIG_HOME"]);
            Assert.DoesNotContain("OPENCODE_CONFIG_DIR", variables.Keys);
            Assert.DoesNotContain("XDG_DATA_HOME", variables.Keys);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    [Fact]
    public void GetEnvironmentVariables_Grok46DoesNotSetGrokHomeOrXdg()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(Path.GetTempPath(), $"viberails-grok-{Guid.NewGuid():N}");
        var service = CreateService(Mock.Of<IFileService>());

        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var variables = service.GetEnvironmentVariables("review", LLM.Grok46);

            Assert.Empty(variables);
            Assert.DoesNotContain("GROK_HOME", variables.Keys);
            Assert.DoesNotContain("XDG_CONFIG_HOME", variables.Keys);
            Assert.DoesNotContain("XDG_DATA_HOME", variables.Keys);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    [Fact]
    public void GetEnvironmentVariables_Glm53UsesIsolatedXdgConfigRoot()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(Path.GetTempPath(), $"viberails-glm53-{Guid.NewGuid():N}");
        var expectedPath = Path.GetFullPath(Path.Combine(configuredEnvRoot, "review"));
        var service = CreateService(Mock.Of<IFileService>());

        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var variables = service.GetEnvironmentVariables("review", LLM.Glm53);

            Assert.Equal(expectedPath, variables["XDG_CONFIG_HOME"]);
            Assert.DoesNotContain("OPENCODE_CONFIG_DIR", variables.Keys);
            Assert.DoesNotContain("XDG_DATA_HOME", variables.Keys);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    [Fact]
    public void OpencodeLauncher_UsesIsolatedXdgConfigRoot()
    {
        var originalEnvPath = ParserConfigs.GetEnvPath();
        var configuredEnvRoot = Path.Combine(Path.GetTempPath(), $"viberails-opencode-{Guid.NewGuid():N}");
        var expectedPath = Path.GetFullPath(Path.Combine(configuredEnvRoot, "review"));
        var launcher = new OpencodeLlmCliLauncher();

        ParserConfigs.SetEnvPath(configuredEnvRoot);

        try
        {
            var variables = launcher.GetEnvironmentVariables("review");

            Assert.Equal(expectedPath, variables["XDG_CONFIG_HOME"]);
            Assert.DoesNotContain("OPENCODE_CONFIG_DIR", variables.Keys);
            Assert.DoesNotContain("XDG_DATA_HOME", variables.Keys);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvPath);
        }
    }

    private static LlmCliEnvironmentService CreateService(IFileService fileService)
    {
        return new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(fileService),
            new CodexLlmCliEnvironment(fileService),
            new AntigravityLlmCliEnvironment(fileService),
            new CopilotLlmCliEnvironment(fileService),
            new OpencodeLlmCliEnvironment(fileService),
            new GrokLlmCliEnvironment(fileService),
            fileService);
    }
}
