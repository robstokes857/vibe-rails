using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Services;
using Xunit;

namespace Tests;

public sealed class InitHookInstallationTests
{
    [Fact]
    public async Task TryInstallGitHooksIfEnabledAsync_RespectsRepositoryOptOut()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"vb_hook_opt_out_{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));

        try
        {
            var hookService = new Mock<IHookInstallationService>(MockBehavior.Strict);
            hookService
                .Setup(service => service.IsAutoInstallDisabledAsync(
                    repoPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["VibeRails:Hooks:AutoInstall"] = "true",
                    ["VibeRails:Hooks:InstallOnStartup"] = "true"
                })
                .Build();
            await using var services = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton(hookService.Object)
                .BuildServiceProvider();

            await global::VibeRails.Init.TryInstallGitHooksIfEnabledAsync(
                services,
                repoPath,
                TestContext.Current.CancellationToken);

            hookService.Verify(service => service.GetStatusAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            hookService.Verify(service => service.InstallHooksAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task TryInstallGitHooksIfEnabledAsync_NeverWritesAHooksPathOutsideTheGitDirectory()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"vb_hook_external_{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));

        try
        {
            var externalHooks = Path.Combine(Path.GetTempPath(), "shared-hooks");
            GitHookFileStatus Missing(string name) => new(
                name, Path.Combine(externalHooks, name), GitHookFileState.Missing, false, "Hook file is missing.");
            var hookService = new Mock<IHookInstallationService>(MockBehavior.Strict);
            hookService
                .Setup(service => service.IsAutoInstallDisabledAsync(repoPath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            hookService
                .Setup(service => service.GetStatusAsync(repoPath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHooksStatus(
                    repoPath,
                    externalHooks,
                    Missing("pre-commit"),
                    Missing("commit-msg"),
                    Missing("post-commit"),
                    HooksPathOutsideGitDirectory: true));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["VibeRails:Hooks:AutoInstall"] = "true",
                    ["VibeRails:Hooks:InstallOnStartup"] = "true"
                })
                .Build();
            await using var services = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton(hookService.Object)
                .BuildServiceProvider();

            await global::VibeRails.Init.TryInstallGitHooksIfEnabledAsync(
                services,
                repoPath,
                TestContext.Current.CancellationToken);

            hookService.Verify(service => service.InstallHooksAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
            hookService.Verify(service => service.InstallHooksAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }
}
