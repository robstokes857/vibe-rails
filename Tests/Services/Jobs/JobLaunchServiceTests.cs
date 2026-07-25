using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using Xunit;

namespace Tests.Services.Jobs;

/// <summary>
/// Covers what a Job actually launches.
///
/// The most important tests here are the verbatim-argument ones. The previous implementation
/// rewrote every Job launch into a non-interactive form (<c>--print</c>, <c>codex exec</c>,
/// <c>--allow-all-tools</c>) and stripped any of the user's saved flags that conflicted. The whole
/// point of the redesign is that it does not: a Job runs the user's Environment exactly as they
/// configured it. That is invisible in the type system, so it is pinned here instead.
/// </summary>
public sealed class JobLaunchServiceTests
{
    private const string MissingProjectPath = @"C:\viberails-tests\does-not-exist";
    private const string ProjectMissingMessage = "The project directory no longer exists";

    // ---- What gets launched -------------------------------------------------------------------

    [Fact]
    public void BuildCliArgs_PassesSavedArgumentsThroughUntouched_IncludingDangerousOnes()
    {
        // If the user chose to skip permissions on the Worker, that is their call and it must reach
        // the CLI. Silently dropping or "correcting" it would be the old behaviour returning.
        var environment = Environment(1, "worker",
            customArgs: "--model opus --dangerously-skip-permissions --permission-mode plan");

        var args = JobLaunchService.BuildCliArgs(environment);

        Assert.Equal(
            ["--model", "opus", "--dangerously-skip-permissions", "--permission-mode", "plan", "Run the nightly review."],
            args);
    }

    [Fact]
    public void BuildCliArgs_DoesNotInjectAnyAutomationFlags()
    {
        var args = JobLaunchService.BuildCliArgs(Environment(1, "worker", customArgs: "--model opus"));

        // The exact flags the old JobCommandBuilder forced in. None of them may come back.
        foreach (var forbidden in new[]
                 {
                     "--print", "-p", "exec", "--ask-for-approval", "--allow-all-tools",
                     "--no-ask-user", "--auto", "--dangerously-bypass-approvals-and-sandbox",
                     "--skip-git-repo-check", "--sandbox"
                 })
        {
            Assert.DoesNotContain(forbidden, args);
        }
    }

    [Fact]
    public void BuildCliArgs_AppendsThePromptUsingTheInteractiveConvention()
    {
        // Copilot takes --interactive=<text>; a positional would be wrong. This is the same
        // convention every other launch path uses, via LlmPromptArgvBuilder.
        var environment = Environment(1, "worker", customArgs: "", llm: LLM.Copilot);

        var args = JobLaunchService.BuildCliArgs(environment);

        Assert.Equal(["--interactive=Run the nightly review."], args);
    }

    [Fact]
    public void BuildCliArgs_LaunchesWithNoPrompt_WhenTheWorkerHasNoInitialMessage()
    {
        // An Environment without an initial message is still perfectly launchable — it just opens
        // the CLI sitting at its prompt, exactly as the Environment screen would.
        var args = JobLaunchService.BuildCliArgs(Environment(1, "worker", customArgs: "--model opus", prompt: "   "));

        Assert.Equal(["--model", "opus"], args);
    }

    [Fact]
    public void BuildVbArgs_OmitsTheDeadline_WhenNoTimeoutWasSet()
    {
        // No timeout is the default: the run lives until the CLI exits or the window is closed.
        var args = JobLaunchService.BuildVbArgs(Run(environmentId: 7, environmentName: "worker", timeoutMinutes: null));

        Assert.Equal(["--job-run", "run-1"], args);
    }

    [Fact]
    public void BuildVbArgs_IncludesTheDeadline_WhenTheUserOptedIn()
    {
        var args = JobLaunchService.BuildVbArgs(Run(environmentId: 7, environmentName: "worker", timeoutMinutes: 45));

        Assert.Equal(["--job-run", "run-1", "--max-runtime", "45"], args);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_LaunchesTheWorkersOwnEnvironmentAndArguments()
    {
        var environment = Environment(7, "nightly", customArgs: "--model opus --dangerously-skip-permissions");
        var repository = RepositoryResolvingById(7, environment);
        var store = LaunchableStore(Run(environmentId: 7, environmentName: "nightly", projectPath: ExistingProjectPath()));
        var launcher = new Mock<ILaunchLLMService>(MockBehavior.Strict);
        launcher
            .Setup(l => l.LaunchInTerminal(
                LLM.Claude, "nightly", It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(new LaunchResult(true, "launched"));

        var launched = await new JobLaunchService(store.Object, repository.Object, launcher.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
        launcher.Verify(l => l.LaunchInTerminal(
            LLM.Claude,
            "nightly",
            It.IsAny<string>(),
            It.Is<string[]>(args => args.Contains("--dangerously-skip-permissions") && args.Contains("--model")),
            It.Is<string[]>(vbArgs => vbArgs.SequenceEqual(new[] { "--job-run", "run-1" })),
            // A Job's window belongs to that one run: no shell is left behind when it finishes.
            false),
            Times.Once);
    }

    // ---- Guards before anything is spawned -----------------------------------------------------

    [Fact]
    public async Task LaunchQueuedRunsAsync_ResolvesTheWorkerById_WhenItWasRenamedAfterTheRunWasQueued()
    {
        // EnvironmentName on the run is only a snapshot from enqueue time. Renaming the Worker
        // before the run launches must not make it fail as nonexistent.
        var repository = RepositoryResolvingById(7, Environment(7, "renamed-worker"));
        var store = LaunchableStore(Run(environmentId: 7, environmentName: "name-at-enqueue-time"));

        await Service(store, repository).LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        repository.Verify(
            r => r.GetEnvironmentByNameAndLlmAsync(It.IsAny<string>(), It.IsAny<LLM>(), It.IsAny<CancellationToken>()),
            Times.Never);
        AssertFailedWith(store, "run-1", ProjectMissingMessage);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_FallsBackToNameAndLlm_WhenTheRunPredatesEnvironmentIds()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync("legacy-worker", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Environment(0, "legacy-worker"));
        var store = LaunchableStore(Run(environmentId: null, environmentName: "legacy-worker"));

        await Service(store, repository).LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        AssertFailedWith(store, "run-1", ProjectMissingMessage);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_FallsBackToNameAndLlm_WhenTheRecordedIdNoLongerResolves()
    {
        // The Worker was deleted and recreated: the id is stale but the name still finds it.
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment?)null);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync("worker", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Environment(12, "worker"));
        var store = LaunchableStore(Run(environmentId: 7, environmentName: "worker"));

        await Service(store, repository).LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        AssertFailedWith(store, "run-1", ProjectMissingMessage);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_FailsTheRun_WhenNeitherIdNorNameResolvesAWorker()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment?)null);
        repository
            .Setup(r => r.GetEnvironmentByNameAndLlmAsync("gone", LLM.Claude, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment?)null);
        var store = LaunchableStore(Run(environmentId: 7, environmentName: "gone"));

        await Service(store, repository).LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        AssertFailedWith(store, "run-1", "no longer exists");
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_SkipsTheRun_WhenAnotherSchedulerAlreadyClaimedTheLaunch()
    {
        // The dashboard scheduler and the OS tick both run. Whoever wins TryMarkLaunchedAsync opens
        // the terminal; the loser must not open a second one or touch the run's outcome.
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var store = LaunchableStore(Run(environmentId: 7, environmentName: "worker"));
        store
            .Setup(s => s.TryMarkLaunchedAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var launched = await Service(store, repository).LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, launched);
        repository.VerifyNoOtherCalls();
        store.Verify(
            s => s.CompleteRunAsync(It.IsAny<string>(), It.IsAny<JobRunStatus>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_StopsAtTheMachineWideCap()
    {
        // Without a default timeout, runs can legitimately stay open for hours. The cap is what
        // stops many jobs coming due at once from burying the desktop in terminal windows.
        var runs = Enumerable.Range(1, JobLaunchService.MaxConcurrentJobTerminals + 2)
            .Select(index => Run(environmentId: 7, environmentName: "worker", id: $"run-{index}", projectPath: ExistingProjectPath()))
            .ToArray();
        var repository = RepositoryResolvingById(7, Environment(7, "worker"));
        var store = LaunchableStore(runs);
        var launcher = new Mock<ILaunchLLMService>();
        launcher
            .Setup(l => l.LaunchInTerminal(It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(new LaunchResult(true, "launched"));

        var launched = await new JobLaunchService(store.Object, repository.Object, launcher.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobLaunchService.MaxConcurrentJobTerminals, launched);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_CountsTerminalsAlreadyOpenAgainstTheCap()
    {
        // Otherwise every tick opens a fresh set of windows on top of the ones still running, and
        // the cap bounds nothing.
        var runs = Enumerable.Range(1, 3)
            .Select(index => Run(environmentId: 7, environmentName: "worker", id: $"run-{index}", projectPath: ExistingProjectPath()))
            .ToArray();
        var repository = RepositoryResolvingById(7, Environment(7, "worker"));
        var store = LaunchableStore(runs);
        store
            .Setup(s => s.CountRunningRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobLaunchService.MaxConcurrentJobTerminals - 1);
        var launcher = new Mock<ILaunchLLMService>();
        launcher
            .Setup(l => l.LaunchInTerminal(It.IsAny<LLM>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(new LaunchResult(true, "launched"));

        var launched = await new JobLaunchService(store.Object, repository.Object, launcher.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
    }

    // ---- Exit codes ----------------------------------------------------------------------------

    [Theory]
    [InlineData(JobRunStatus.Succeeded, 0)]
    [InlineData(JobRunStatus.Failed, 1)]
    [InlineData(JobRunStatus.TimedOut, 2)]
    [InlineData(JobRunStatus.Cancelled, 3)]
    [InlineData(JobRunStatus.Interrupted, 4)]
    [InlineData(JobRunStatus.Queued, 5)]
    public void ToExitCode_ReportsTheOutcomeDistinctly(JobRunStatus status, int expected)
    {
        // A supervisor must be able to tell what happened without reading SQLite; the old
        // implementation always returned 0 regardless of outcome.
        Assert.Equal(expected, JobRunner.ToExitCode(status));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static JobLaunchService Service(Mock<IJobStore> store, Mock<IRepository> repository) =>
        new(store.Object, repository.Object, Mock.Of<ILaunchLLMService>());

    private static Mock<IRepository> RepositoryResolvingById(int id, LLM_Environment environment)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnvironmentByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        return repository;
    }

    private static Mock<IJobStore> LaunchableStore(params JobRunRecord[] runs)
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(s => s.GetLaunchableRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs);
        store
            .Setup(s => s.CountRunningRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        store
            .Setup(s => s.TryMarkLaunchedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        store
            .Setup(s => s.CompleteRunAsync(It.IsAny<string>(), It.IsAny<JobRunStatus>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static void AssertFailedWith(Mock<IJobStore> store, string runId, string expectedFragment) =>
        store.Verify(
            s => s.CompleteRunAsync(
                runId,
                JobRunStatus.Failed,
                null,
                It.Is<string>(message => message != null && message.Contains(expectedFragment, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);

    // The launch path checks the directory exists, so tests that need to reach the launcher point
    // at a directory that certainly does; guard tests point at one that certainly doesn't.
    private static string ExistingProjectPath() => AppContext.BaseDirectory;

    private static LLM_Environment Environment(
        int id,
        string name,
        string prompt = "Run the nightly review.",
        string customArgs = "",
        LLM llm = LLM.Claude) =>
        new()
        {
            Id = id,
            CustomName = name,
            LLM = llm,
            CustomArgs = customArgs,
            CustomPrompt = prompt
        };

    private static JobRunRecord Run(
        int? environmentId,
        string? environmentName,
        int? timeoutMinutes = null,
        string id = "run-1",
        string? projectPath = null) => new(
        Id: id,
        JobId: 1,
        TriggerKind: JobTriggerKind.Manual,
        TriggerKey: "manual",
        Status: JobRunStatus.Queued,
        JobName: "Nightly review",
        ProjectPath: projectPath ?? MissingProjectPath,
        Llm: LLM.Claude,
        EnvironmentId: environmentId,
        EnvironmentName: environmentName,
        TimeoutMinutes: timeoutMinutes,
        SessionId: null,
        QueuedUtc: new DateTime(2026, 7, 24, 9, 0, 0, DateTimeKind.Utc),
        StartedUtc: null,
        EndedUtc: null,
        ExitCode: null,
        ErrorMessage: null,
        CancelRequested: false,
        OwnerProcessId: null);
}
