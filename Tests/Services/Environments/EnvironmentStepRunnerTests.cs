using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;
using VibeRails.Services.Environments.Steps;
using Xunit;

namespace Tests.Services.Environments;

/// <summary>
/// The runner is deliberately thin, and all the process mechanics live behind ICliWrapper — so
/// every one of these runs against a fake and starts no processes at all.
/// </summary>
public class EnvironmentStepRunnerTests
{
    private const int EnvironmentId = 7;
    private const string WorkDir = @"C:\source\clone";

    [Fact]
    public async Task RunsEnabledStepsInPositionOrder()
    {
        var cli = new FakeCliWrapper();
        var runner = BuildRunner(cli, Step(0, "git pull"), Step(1, "npm ci"), Step(2, "dotnet build"));

        var summary = await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(summary.Success);
        Assert.Equal(3, summary.StepsRun);
        Assert.Null(summary.FailedStep);
        Assert.Equal(["git pull", "npm ci", "dotnet build"], cli.Requests.Select(r => r.ScriptBody));
    }

    [Fact]
    public async Task StopsAtTheFirstFailureAndReportsWhichStep()
    {
        var cli = new FakeCliWrapper { ExitCodeFor = body => body == "npm ci" ? 1 : 0 };
        var runner = BuildRunner(cli, Step(0, "git pull"), Step(1, "npm ci", name: "Install"), Step(2, "dotnet build"));

        var summary = await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(summary.Success);
        Assert.Equal(2, summary.StepsRun);
        Assert.Equal("Install", summary.FailedStep?.DisplayName);
        Assert.Equal(1, summary.FailedResult?.ExitCode);
        // The third step must never have been started.
        Assert.Equal(["git pull", "npm ci"], cli.Requests.Select(r => r.ScriptBody));
        Assert.Equal("Step \"Install\" exited with code 1. ", summary.FailureMessage + " ");
    }

    [Fact]
    public async Task StepsNeverOverlap()
    {
        var cli = new FakeCliWrapper { DelayPerRun = TimeSpan.FromMilliseconds(30) };
        var runner = BuildRunner(cli, Step(0, "one"), Step(1, "two"), Step(2, "three"));

        await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        // The whole point of steps is that they are sequential and blocking.
        Assert.Equal(1, cli.MaxConcurrent);
    }

    [Fact]
    public async Task NoEnabledSteps_IsASuccessThatRunsNothing()
    {
        var cli = new FakeCliWrapper();
        var runner = BuildRunner(cli);

        var summary = await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PostExit, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(summary.Success);
        Assert.Equal(0, summary.StepsRun);
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task PassesThroughWorkingDirectoryMinimizedFlagAndTimeout()
    {
        var cli = new FakeCliWrapper();
        var step = Step(0, "npm ci");
        step.StartMinimized = true;
        step.TimeoutSeconds = 45;
        var runner = BuildRunner(cli, step);

        await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(cli.Requests);
        // The workspace-resolved directory, so a clone environment runs its steps in the clone.
        Assert.Equal(WorkDir, request.WorkingDirectory);
        Assert.True(request.StartMinimized);
        Assert.Equal(TimeSpan.FromSeconds(45), request.Timeout);
    }

    [Theory]
    [InlineData(0, EnvironmentStep.MinTimeoutSeconds)]
    [InlineData(-5, EnvironmentStep.MinTimeoutSeconds)]
    [InlineData(999_999, EnvironmentStep.MaxTimeoutSeconds)]
    public async Task OutOfRangeStoredTimeout_IsClamped(int stored, int expected)
    {
        // The API clamps on write, but a row from an older build (or hand-edited in the DB) must
        // not produce a zero timeout that fails every step instantly.
        var cli = new FakeCliWrapper();
        var step = Step(0, "npm ci");
        step.TimeoutSeconds = stored;
        var runner = BuildRunner(cli, step);

        await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(expected), Assert.Single(cli.Requests).Timeout);
    }

    [Fact]
    public async Task ReportsProgressForEveryStepIncludingTheFailure()
    {
        var cli = new FakeCliWrapper { ExitCodeFor = body => body == "two" ? 2 : 0 };
        var runner = BuildRunner(cli, Step(0, "one"), Step(1, "two"), Step(2, "three"));
        var progress = new List<StepProgress>();

        await runner.RunPhaseAsync(
            EnvironmentId,
            EnvironmentStepPhase.PreLaunch,
            WorkDir,
            update => { progress.Add(update); return ValueTask.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                StepProgressKind.Started, StepProgressKind.Succeeded,
                StepProgressKind.Started, StepProgressKind.Failed
            ],
            progress.Select(p => p.Kind));
        Assert.All(progress, update => Assert.Equal(3, update.Total));
        Assert.Equal(2, progress[^1].ExitCode);
    }

    [Fact]
    public async Task AThrowingProgressCallbackDoesNotFailTheRun()
    {
        // Progress is advisory: a UI callback that throws must not take the launch with it.
        var cli = new FakeCliWrapper();
        var runner = BuildRunner(cli, Step(0, "one"));

        var summary = await runner.RunPhaseAsync(
            EnvironmentId,
            EnvironmentStepPhase.PreLaunch,
            WorkDir,
            _ => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        Assert.True(summary.Success);
        Assert.Single(cli.Requests);
    }

    [Fact]
    public async Task ATimedOutStepStopsThePhaseEvenThoughItsExitCodeIsUnknown()
    {
        var cli = new FakeCliWrapper { TimeOutOn = "hangs" };
        var runner = BuildRunner(cli, Step(0, "hangs"), Step(1, "never"));

        var summary = await runner.RunPhaseAsync(
            EnvironmentId, EnvironmentStepPhase.PreLaunch, WorkDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(summary.Success);
        Assert.True(summary.FailedResult?.TimedOut);
        Assert.Single(cli.Requests);
    }

    private static EnvironmentStep Step(int position, string command, string name = "") => new()
    {
        Id = position + 1,
        EnvironmentId = EnvironmentId,
        Phase = EnvironmentStepPhase.PreLaunch,
        Position = position,
        Name = name,
        Command = command,
        TimeoutSeconds = EnvironmentStep.DefaultTimeoutSeconds,
        Enabled = true
    };

    private static EnvironmentStepRunner BuildRunner(ICliWrapper cli, params EnvironmentStep[] steps)
    {
        // GetEnabledStepsAsync does the Enabled filter and Position ordering in SQL (covered by
        // EnvironmentStepsSqlTests), so the fake returns exactly what the runner would be handed.
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetEnabledStepsAsync(
                It.IsAny<int>(), It.IsAny<EnvironmentStepPhase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(steps.ToList());

        return new EnvironmentStepRunner(repository.Object, cli);
    }

    private sealed class FakeCliWrapper : ICliWrapper
    {
        private int _concurrent;

        public List<CliTerminalRequest> Requests { get; } = [];
        public int MaxConcurrent { get; private set; }
        public Func<string, int> ExitCodeFor { get; init; } = _ => 0;
        public TimeSpan DelayPerRun { get; init; } = TimeSpan.Zero;
        public string? TimeOutOn { get; init; }

        public Task<CliResult> RunAsync(
            CliRequest request,
            Func<CliOutputLine, ValueTask>? onLine = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Steps must run in a visible terminal window.");

        public async Task<CliResult> RunInNewTerminalAsync(
            CliTerminalRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _concurrent);
            if (now > MaxConcurrent) MaxConcurrent = now;
            Requests.Add(request);

            if (DelayPerRun > TimeSpan.Zero)
                await Task.Delay(DelayPerRun, cancellationToken);

            Interlocked.Decrement(ref _concurrent);

            var timedOut = TimeOutOn is not null && request.ScriptBody == TimeOutOn;
            return new CliResult(
                timedOut ? -1 : ExitCodeFor(request.ScriptBody),
                timedOut,
                Cancelled: false,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                Duration: TimeSpan.Zero,
                CommandLine: request.ScriptBody);
        }
    }
}
