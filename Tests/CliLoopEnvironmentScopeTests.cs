using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests;

/// <summary>
/// <c>FindEnvironmentByNameAsync</c> is a global lookup — environment names are unique across the
/// table but are not keyed by project. A hand-typed <c>vb --env &lt;name&gt;</c> therefore has to
/// check the row against the directory it is being launched from; without it, a name learned from
/// anywhere launches that environment's arguments, permission flags, and per-environment
/// credentials directory against whatever repository the user happens to be standing in.
/// </summary>
public sealed class CliLoopEnvironmentScopeTests
{
    private sealed class ReachedThePromptStage : Exception;

    [Fact]
    public async Task AnEnvironmentBelongingToAnotherProject_IsRefused()
    {
        var here = Path.Combine(Path.GetTempPath(), "cliloop-here");
        var (services, _) = BuildServices(Environment("other-project-env", Path.Combine(Path.GetTempPath(), "cliloop-elsewhere")));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliLoop.RunTerminalWithWebAsync(
                Args("other-project-env", here),
                services,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("belongs to another project", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(here, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRefusalHappensBeforeAnyOfTheEnvironmentsStepsRun()
    {
        // The Initial Message is resolved a few lines further down, and resolving one executes
        // every {{step:<id>}} it references. A rejected environment must not get that far.
        var (services, placeholders) = BuildServices(
            Environment("other-project-env", Path.Combine(Path.GetTempPath(), "cliloop-elsewhere")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliLoop.RunTerminalWithWebAsync(
                Args("other-project-env", Path.Combine(Path.GetTempPath(), "cliloop-here")),
                services,
                cancellationToken: TestContext.Current.CancellationToken));

        placeholders.Verify(
            item => item.ResolveAsync(
                It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnEnvironmentInThisProject_IsLaunched()
    {
        // The check has to reject the right thing and only that thing — the sentinel throw from
        // the prompt stage is how this test observes the launch proceeding without spawning a PTY.
        var here = Path.Combine(Path.GetTempPath(), "cliloop-here");
        var (services, _) = BuildServices(Environment("my-env", here), throwAtPromptStage: true);

        await Assert.ThrowsAsync<ReachedThePromptStage>(() =>
            CliLoop.RunTerminalWithWebAsync(
                Args("my-env", here),
                services,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEnvironmentThatPredatesProjectScoping_IsStillLaunchable()
    {
        // Null ProjectPath means "created before scoping existed". There is no backfill, so
        // refusing these would strand every environment a user already had.
        var (services, _) = BuildServices(Environment("legacy-env", projectPath: null), throwAtPromptStage: true);

        await Assert.ThrowsAsync<ReachedThePromptStage>(() =>
            CliLoop.RunTerminalWithWebAsync(
                Args("legacy-env", Path.Combine(Path.GetTempPath(), "cliloop-here")),
                services,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownName_SaysSoRatherThanFallingBackToACli()
    {
        var (services, _) = BuildServices(environment: null);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliLoop.RunTerminalWithWebAsync(
                Args("no-such-env", Path.Combine(Path.GetTempPath(), "cliloop-here")),
                services,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unknown CLI or environment", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnvIdLaunch_SkipsTheNameLookupEntirely()
    {
        // --env-id comes from a process that already resolved the row and checked it against the
        // project (and may have swapped WorkDir to a clone, which is not the project directory).
        // Re-checking it here would refuse legitimate clone-mode launches.
        var elsewhere = Path.Combine(Path.GetTempPath(), "cliloop-elsewhere");
        var environment = Environment("other-project-env", elsewhere);
        environment.Id = 7;
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        var (services, _) = BuildServices(repository, throwAtPromptStage: true);

        var args = Args("other-project-env", Path.Combine(Path.GetTempPath(), "cliloop-here"));
        args.EnvId = 7;

        await Assert.ThrowsAsync<ReachedThePromptStage>(() =>
            CliLoop.RunTerminalWithWebAsync(args, services, cancellationToken: TestContext.Current.CancellationToken));

        repository.Verify(
            item => item.FindEnvironmentByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ParsedArgs Args(string environmentName, string workDir) => new()
    {
        IsLMBootstrap = true,
        LMBootstrapCli = environmentName,
        // Set explicitly so the git-root probe never runs: the check compares against this value,
        // and letting it fall back to the test runner's own directory would make the result
        // depend on where the suite was started from.
        WorkDir = workDir,
    };

    private static LLM_Environment Environment(string name, string? projectPath) => new()
    {
        Id = 1,
        CustomName = name,
        LLM = LLM.Claude,
        ProjectPath = projectPath,
        CustomPrompt = "Initial message with a {{step:2a1f0c4e-0000-4000-8000-000000000001}} reference",
    };

    private static (IServiceProvider Services, Mock<IPromptPlaceholderService> Placeholders) BuildServices(
        LLM_Environment? environment,
        bool throwAtPromptStage = false)
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.FindEnvironmentByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        return BuildServices(repository, throwAtPromptStage);
    }

    private static (IServiceProvider Services, Mock<IPromptPlaceholderService> Placeholders) BuildServices(
        Mock<IRepository> repository,
        bool throwAtPromptStage)
    {
        var placeholders = new Mock<IPromptPlaceholderService>();
        if (throwAtPromptStage)
        {
            placeholders
                .Setup(item => item.ResolveAsync(
                    It.IsAny<string>(), It.IsAny<PromptPlaceholderContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ReachedThePromptStage());
        }

        var services = new ServiceCollection();
        services.AddSingleton(repository.Object);
        services.AddSingleton(placeholders.Object);
        services.AddSingleton<ILlmParser, LlmParser>();
        services.AddSingleton(new Mock<ITerminalSessionService>().Object);
        // TerminalRunner is a concrete class, so it comes from real DI with mocked collaborators.
        // Nothing in these tests reaches RunCliWithWebAsync — every case ends at the scope check
        // or the prompt stage, both of which are ahead of it.
        services.AddSingleton(new Mock<ITerminalStateService>().Object);
        services.AddSingleton(new Mock<ICommandService>().Object);
        services.AddSingleton(new Mock<ILocalToolApiContext>().Object);
        services.AddSingleton(new Mock<ILlmProxySessionState>().Object);
        services.AddSingleton(new Mock<IAutomationConsumer>().Object);
        services.AddSingleton(new Mock<IEnvironmentStepRunner>().Object);
        services.AddSingleton(new Mock<IAppEventBus>().Object);
        services.AddSingleton<TerminalRunner>();

        return (services.BuildServiceProvider(), placeholders);
    }
}
