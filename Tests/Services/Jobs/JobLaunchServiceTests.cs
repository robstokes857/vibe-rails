using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobLaunchServiceTests
{
    private const string MissingProjectPath = @"C:\viberails-tests\does-not-exist";

    [Fact]
    public void BuildVbArgs_OmitsTheDeadline_WhenNoTimeoutWasSet()
    {
        var args = JobLaunchService.BuildVbArgs(Run(timeoutMinutes: null));

        Assert.Equal(["--job-run", "run-1"], args);
    }

    [Fact]
    public void BuildVbArgs_IncludesTheDeadline_WhenTheUserOptedIn()
    {
        var args = JobLaunchService.BuildVbArgs(Run(timeoutMinutes: 45));

        Assert.Equal(["--job-run", "run-1", "--max-runtime", "45"], args);
    }

    [Fact]
    public void BuildStandaloneVbArgs_IncludesTheWorkingDirectoryWithoutAnEnvironment()
    {
        var run = Run(
            timeoutMinutes: 15,
            projectPath: ExistingProjectPath(),
            llm: LLM.NotSet,
            environmentId: null,
            environmentName: null,
            actions: [ScriptAction("action-1", 0)]);

        Assert.Equal(
            ["--workdir", run.ProjectPath, "--job-run", "run-1", "--max-runtime", "15"],
            JobLaunchService.BuildStandaloneVbArgs(run));
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_UsesTheSameEnvironmentLaunchRequestAsTheApi()
    {
        var run = Run(projectPath: ExistingProjectPath());
        var store = LaunchableStore(run);
        var pipeline = SuccessfulPipeline();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
        pipeline.Verify(p => p.LaunchAsync(
            LLM.Claude,
            It.Is<LaunchCliRequest>(request =>
                request.WorkingDirectory == run.ProjectPath
                && request.EnvironmentName == "nightly"
                && request.Args != null
                && request.Args.Length == 0),
            run.ProjectPath,
            It.Is<string[]>(args => args.SequenceEqual(new[] { "--job-run", "run-1" })),
            false,
            7,
            false,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_ScriptOnlyWorkflowUsesTheNeutralNativeTerminalLauncher()
    {
        var run = Run(
            projectPath: ExistingProjectPath(),
            llm: LLM.NotSet,
            environmentId: null,
            environmentName: null,
            actions: [ScriptAction("script-1", 0)]);
        var store = LaunchableStore(run);
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);
        var process = new Mock<IJobProcessLauncher>(MockBehavior.Strict);
        process
            .Setup(launcher => launcher.Launch(
                run.ProjectPath,
                It.Is<IReadOnlyList<string>>(arguments => arguments.SequenceEqual(
                    new[] { "--workdir", run.ProjectPath, "--job-run", run.Id })),
                false))
            .Returns(new LaunchResult(true, "launched"));

        var launched = await new JobLaunchService(store.Object, pipeline.Object, process.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
        pipeline.VerifyNoOtherCalls();
        process.VerifyAll();
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_MixedWorkflowUsesTheWorkerActionForWorkspaceResolution()
    {
        var run = Run(
            projectPath: ExistingProjectPath(),
            llm: LLM.NotSet,
            environmentId: null,
            environmentName: null,
            actions:
            [
                ScriptAction("script-1", 0),
                WorkerAction("worker-1", 1, 19, "workspace worker", LLM.Codex),
                ScriptAction("script-2", 2)
            ]);
        var store = LaunchableStore(run);
        var pipeline = SuccessfulPipeline();
        var process = UnusedProcessLauncher();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, process.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
        pipeline.Verify(candidate => candidate.LaunchAsync(
            LLM.Codex,
            It.Is<LaunchCliRequest>(request => request.EnvironmentName == "workspace worker"),
            run.ProjectPath,
            It.Is<string[]>(arguments => arguments.SequenceEqual(new[] { "--job-run", run.Id })),
            false,
            19,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
        process.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_RejectsASnapshotWithMoreThanOneWorker()
    {
        var run = Run(
            projectPath: ExistingProjectPath(),
            actions:
            [
                WorkerAction("worker-1", 0, 7, "one", LLM.Claude),
                WorkerAction("worker-2", 1, 8, "two", LLM.Codex)
            ]);
        var store = LaunchableStore(run);
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);
        var process = UnusedProcessLauncher();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, process.Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, launched);
        AssertFailedWith(store, "more than one Worker");
        pipeline.VerifyNoOtherCalls();
        process.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_ForwardsTheMinimizedPreference()
    {
        var run = Run(projectPath: ExistingProjectPath(), launchMinimized: true);
        var store = LaunchableStore(run);
        var pipeline = SuccessfulPipeline();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
        pipeline.Verify(p => p.LaunchAsync(
            run.Llm,
            It.IsAny<LaunchCliRequest>(),
            run.ProjectPath,
            It.IsAny<string[]>(),
            false,
            run.EnvironmentId,
            true,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_FailsTheRun_WhenTheSharedPipelineRejectsTheLaunch()
    {
        var store = LaunchableStore(Run(projectPath: ExistingProjectPath()));
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);
        pipeline
            .Setup(p => p.LaunchAsync(
                It.IsAny<LLM>(),
                It.IsAny<LaunchCliRequest>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                false,
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(false, "Environment was not found."));

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, launched);
        AssertFailedWith(store, "Environment was not found");
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_FailsBeforeDispatch_WhenProjectIsGone()
    {
        var store = LaunchableStore(Run());
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, launched);
        AssertFailedWith(store, "project directory no longer exists");
        pipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_SkipsTheRun_WhenAnotherHostAlreadyClaimedTheLaunch()
    {
        var store = LaunchableStore(Run(projectPath: ExistingProjectPath()));
        store
            .Setup(s => s.TryMarkLaunchedAsync("run-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, launched);
        pipeline.VerifyNoOtherCalls();
        store.Verify(
            s => s.CompleteRunAsync(
                It.IsAny<string>(),
                It.IsAny<JobRunStatus>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_StopsAtTheMachineWideCap()
    {
        var runs = Enumerable.Range(1, JobLaunchService.MaxConcurrentJobTerminals + 2)
            .Select(index => Run(id: $"run-{index}", projectPath: ExistingProjectPath()))
            .ToArray();
        var store = LaunchableStore(runs);
        var pipeline = SuccessfulPipeline();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobLaunchService.MaxConcurrentJobTerminals, launched);
    }

    [Fact]
    public async Task LaunchQueuedRunsAsync_CountsAlreadyOpenRunsAgainstTheCap()
    {
        var store = LaunchableStore(
            Run(id: "run-1", projectPath: ExistingProjectPath()),
            Run(id: "run-2", projectPath: ExistingProjectPath()),
            Run(id: "run-3", projectPath: ExistingProjectPath()));
        store
            .Setup(s => s.CountRunningRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(JobLaunchService.MaxConcurrentJobTerminals - 1);
        var pipeline = SuccessfulPipeline();

        var launched = await new JobLaunchService(store.Object, pipeline.Object, UnusedProcessLauncher().Object)
            .LaunchQueuedRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, launched);
    }

    [Theory]
    [InlineData(JobRunStatus.Succeeded, 0)]
    [InlineData(JobRunStatus.Failed, 1)]
    [InlineData(JobRunStatus.TimedOut, 2)]
    [InlineData(JobRunStatus.Cancelled, 3)]
    [InlineData(JobRunStatus.Interrupted, 4)]
    [InlineData(JobRunStatus.Queued, 5)]
    public void ToExitCode_ReportsTheOutcomeDistinctly(JobRunStatus status, int expected)
    {
        Assert.Equal(expected, JobRunOutcome.ToExitCode(status));
    }

    private static Mock<IEnvironmentLaunchService> SuccessfulPipeline()
    {
        var pipeline = new Mock<IEnvironmentLaunchService>(MockBehavior.Strict);
        pipeline
            .Setup(p => p.LaunchAsync(
                It.IsAny<LLM>(),
                It.IsAny<LaunchCliRequest>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                false,
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, "launched"));
        return pipeline;
    }

    private static Mock<IJobProcessLauncher> UnusedProcessLauncher() =>
        new(MockBehavior.Strict);

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
            .Setup(s => s.CompleteRunAsync(
                It.IsAny<string>(),
                It.IsAny<JobRunStatus>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static void AssertFailedWith(Mock<IJobStore> store, string expectedFragment) =>
        store.Verify(s => s.CompleteRunAsync(
            "run-1",
            JobRunStatus.Failed,
            null,
            It.Is<string>(message => message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()),
            Times.Once);

    private static string ExistingProjectPath() => AppContext.BaseDirectory;

    private static JobRunRecord Run(
        int? timeoutMinutes = null,
        string id = "run-1",
        string? projectPath = null,
        bool launchMinimized = false,
        LLM llm = LLM.Claude,
        int? environmentId = 7,
        string? environmentName = "nightly",
        IReadOnlyList<JobRunActionRecord>? actions = null) => new(
        Id: id,
        JobId: 1,
        TriggerKind: JobTriggerKind.Manual,
        TriggerKey: $"manual:{id}",
        Status: JobRunStatus.Queued,
        JobName: "Nightly review",
        ProjectPath: projectPath ?? MissingProjectPath,
        Llm: llm,
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
        OwnerProcessId: null,
        LaunchMinimized: launchMinimized,
        Actions: actions);

    private static JobRunActionRecord ScriptAction(string id, int position) => new(
        id,
        "run-1",
        id,
        position,
        JobActionKind.Script,
        JobRunActionStatus.Pending,
        null,
        null,
        LLM.NotSet,
        $"scripts/{id}.py",
        JobScriptRuntime.Python,
        [],
        null,
        null,
        new string('a', 64),
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        string.Empty);

    private static JobRunActionRecord WorkerAction(
        string id,
        int position,
        int environmentId,
        string environmentName,
        LLM llm) => new(
        id,
        "run-1",
        id,
        position,
        JobActionKind.Worker,
        JobRunActionStatus.Pending,
        environmentId,
        environmentName,
        llm,
        null,
        null,
        [],
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        string.Empty);
}
