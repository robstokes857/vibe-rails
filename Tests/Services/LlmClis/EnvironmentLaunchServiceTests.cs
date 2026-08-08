using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Workspaces;
using Xunit;

namespace Tests.Services.LlmClis;

public sealed class EnvironmentLaunchServiceTests
{
    /// <summary>
    /// A workspace service that fails on any call. Every environment in these tests is in the
    /// default Project mode, so reaching workspace resolution at all would be the bug — strict
    /// mode turns "launch quietly started cloning" into a test failure instead of a slow test.
    /// </summary>
    private static IRunWorkspaceService NoWorkspace() =>
        new Mock<IRunWorkspaceService>(MockBehavior.Strict).Object;

    [Fact]
    public async Task LaunchAsync_AppliesSavedEnvironmentOnce_AndTouchesOnlyRecency()
    {
        var environment = Environment(
            id: 7,
            name: "nightly",
            customArgs: "--model opus --dangerously-skip-permissions",
            prompt: "Review this repository.",
            lastUsedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync(
                "nightly", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        repository
            .Setup(r => r.TouchEnvironmentLastUsedAsync(7, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Claude,
                "nightly",
                AppContext.BaseDirectory,
                It.Is<string[]>(args => args.SequenceEqual(new[]
                {
                    "--model", "opus", "--dangerously-skip-permissions",
                    "--caller-flag", "Review this repository."
                })),
                null,
                true,
                false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, "nightly", ["--caller-flag"]),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // Recency must be a timestamp-only touch. A full-record UpdateEnvironmentAsync here
        // would write back the columns read moments earlier, silently reverting any edit
        // the user saved between the read and the launch.
        repository.Verify(
            r => r.UpdateEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()),
            Times.Never);
        repository.VerifyAll();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_StillLaunches_WhenRecencyBookkeepingFails()
    {
        var environment = Environment(7, "nightly", "", "Run it.", DateTime.UtcNow);
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync(
                "nightly", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        repository
            .Setup(r => r.TouchEnvironmentLastUsedAsync(7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Claude,
                "nightly",
                AppContext.BaseDirectory,
                It.IsAny<string[]>(),
                null,
                true,
                false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, "nightly", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        // A busy state.db must not veto an otherwise valid launch.
        Assert.True(result.Success);
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_ReportsInfrastructureFaultsGenerically_ButValidationVerbatim()
    {
        // Infrastructure fault: the repository read blows up. The raw message (which can
        // carry DB paths and internals) must not surface to the caller.
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync(
                "nightly", LLM.Claude, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(@"SQLite Error 14: unable to open C:\secret\state.db"));

        var faulted = await new EnvironmentLaunchService(repository.Object, Mock.Of<ILaunchLLMService>(), NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, "nightly", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(faulted.Success);
        Assert.DoesNotContain("state.db", faulted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal error", faulted.Message, StringComparison.OrdinalIgnoreCase);

        // Validation fault: an unparseable CustomArgs string. That message is written for
        // the user and must surface verbatim so they can fix the arguments.
        var badArgs = Environment(7, "nightly", "\"unterminated", "Run it.", DateTime.UtcNow);
        var validating = new Mock<IRepository>(MockBehavior.Strict);
        validating
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync(
                "nightly", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync(badArgs);

        var rejected = await new EnvironmentLaunchService(validating.Object, Mock.Of<ILaunchLLMService>(), NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, "nightly", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(rejected.Success);
        Assert.Contains("unterminated", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_ResolvesAutomationEnvironmentByStableId()
    {
        var environment = Environment(7, "renamed-worker", "", "Run it.", DateTime.UtcNow);
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        repository
            .Setup(r => r.TouchEnvironmentLastUsedAsync(7, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Claude,
                "renamed-worker",
                AppContext.BaseDirectory,
                It.Is<string[]>(args => args.SequenceEqual(new[] { "Run it." })),
                It.Is<string[]>(args => args.SequenceEqual(new[] { "--job-run", "run-1" })),
                false,
                false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, "old-name", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            vbArgs: ["--job-run", "run-1"],
            keepTerminalOpen: false,
            environmentId: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        repository.Verify(
            r => r.GetEnvironmentByNameAndLlmAsync(
                It.IsAny<string>(), It.IsAny<LLM>(), It.IsAny<CancellationToken>()),
            Times.Never);
        repository.VerifyAll();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_RejectsAMissingNamedEnvironment_InsteadOfLaunchingTheBaseCli()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync(
                "gone", LLM.Codex, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment?)null);
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Codex,
            new LaunchCliRequest(AppContext.BaseDirectory, "gone", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        launcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchAsync_RejectsAMissingStableId_InsteadOfFallingBackToAReusedName()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment?)null);
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Codex,
            new LaunchCliRequest(AppContext.BaseDirectory, "reused-name", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            environmentId: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        repository.Verify(
            r => r.GetEnvironmentByNameAndLlmAsync(
                It.IsAny<string>(), It.IsAny<LLM>(), It.IsAny<CancellationToken>()),
            Times.Never);
        repository.VerifyAll();
        launcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchAsync_BaseCliPassesCallerArgumentsWithoutTouchingEnvironmentStorage()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Codex,
                null,
                AppContext.BaseDirectory,
                It.Is<string[]>(args => args.SequenceEqual(new[] { "--model", "gpt-5.6-sol" })),
                null,
                true,
                false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Codex,
            new LaunchCliRequest(AppContext.BaseDirectory, null, ["--model", "gpt-5.6-sol"]),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        repository.VerifyNoOtherCalls();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_WhitespaceEnvironmentName_IsNormalizedToTheBaseCli()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Codex,
                null,
                AppContext.BaseDirectory,
                Array.Empty<string>(),
                null,
                true,
                false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Codex,
            new LaunchCliRequest(AppContext.BaseDirectory, "   ", []),
            fallbackWorkingDirectory: Path.GetTempPath(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        repository.VerifyNoOtherCalls();
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_RefusesAnEnvironmentBelongingToAnotherProject()
    {
        var environment = Environment(
            id: 7,
            name: "nightly",
            customArgs: "--dangerously-skip-permissions",
            prompt: "Review this repository.",
            lastUsedUtc: DateTime.UtcNow);
        environment.ProjectPath = Path.Combine(Path.GetTempPath(), "some-other-project");

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);

        // Strict: nothing may be spawned. The saved id resolves to a real row, so without the
        // scope check this would launch another project's environment — with its arguments and
        // its permission flags — inside this project's directory.
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, null, []),
            fallbackWorkingDirectory: AppContext.BaseDirectory,
            environmentId: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("was not found", result.Message, StringComparison.OrdinalIgnoreCase);
        launcher.VerifyAll();
    }

    [Fact]
    public async Task LaunchAsync_AllowsAnEnvironmentThatPredatesProjectScoping()
    {
        var environment = Environment(
            id: 8,
            name: "legacy",
            customArgs: "",
            prompt: "",
            lastUsedUtc: DateTime.UtcNow);
        // Null scope is the no-backfill state — it must keep working in every project.
        environment.ProjectPath = null;

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        repository
            .Setup(r => r.TouchEnvironmentLastUsedAsync(8, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var launcher = new Mock<ILaunchLLMService>();
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Claude, "legacy", AppContext.BaseDirectory,
                It.IsAny<string[]>(), null, true, false))
            .Returns(new LaunchResult(true, "launched"));

        var result = await new EnvironmentLaunchService(repository.Object, launcher.Object, NoWorkspace()).LaunchAsync(
            LLM.Claude,
            new LaunchCliRequest(AppContext.BaseDirectory, null, []),
            fallbackWorkingDirectory: AppContext.BaseDirectory,
            environmentId: 8,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    private static LLM_Environment Environment(
        int id,
        string name,
        string customArgs,
        string prompt,
        DateTime lastUsedUtc) =>
        new()
        {
            Id = id,
            CustomName = name,
            LLM = LLM.Claude,
            CustomArgs = customArgs,
            CustomPrompt = prompt,
            LastUsedUTC = lastUsedUtc,
            // Scoped to the directory these tests launch from, so the existing cases exercise
            // the allow path rather than accidentally relying on a null scope.
            ProjectPath = AppContext.BaseDirectory
        };
}
