using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobRunnerWorkflowTests
{
    [Fact]
    public void BoardCardKey_IsOpaqueAndComesOnlyFromABoardLaneTrigger()
    {
        var run = Run([]);

        Assert.Equal(
            "VB-23",
            JobRunner.GetBoardCardKey(run with
            {
                TriggerKind = JobTriggerKind.BoardLane,
                TriggerKey = "board-lane:VB-23:review:event-1"
            }));
        Assert.Equal(
            "xyz-90909-jkjhfdk",
            JobRunner.GetBoardCardKey(run with
            {
                TriggerKind = JobTriggerKind.BoardLane,
                TriggerKey = "board-lane:xyz-90909-jkjhfdk:review:event-1"
            }));
        Assert.Null(JobRunner.GetBoardCardKey(run with
        {
            TriggerKind = JobTriggerKind.Manual,
            TriggerKey = "board-lane:xyz-90909-jkjhfdk:review:event-1"
        }));
        Assert.Null(JobRunner.GetBoardCardKey(run with
        {
            TriggerKind = JobTriggerKind.BoardLane,
            TriggerKey = "board-lane::review:event-1"
        }));
        Assert.Null(JobRunner.GetBoardCardKey(run with
        {
            TriggerKind = JobTriggerKind.BoardLane,
            TriggerKey = "board-lane:xyz-90909-jkjhfdk"
        }));
        Assert.Null(JobRunner.GetBoardCardKey(run with
        {
            TriggerKind = JobTriggerKind.Manual,
            TriggerKey = "retry:original-run:event-1"
        }));
    }

    [Fact]
    public async Task RunAsync_ExecutesScriptActionsInPositionOrderAndPersistsTheirOutput()
    {
        var first = ScriptAction("first", position: 0, "scripts/first.py");
        var second = ScriptAction("second", position: 1, "scripts/second.py");
        var run = Run([second, first]); // Deliberately unsorted input proves Position is authoritative.
        var started = new List<string>();
        var completed = new List<string>();
        var store = StoreFor(run);
        store
            .Setup(candidate => candidate.StartRunActionAsync(
                run.Id,
                It.IsAny<string>(),
                CancellationToken.None))
            .Callback<string, string, CancellationToken>((_, actionId, _) => started.Add(actionId))
            .ReturnsAsync(true);
        store
            .Setup(candidate => candidate.CompleteRunActionAsync(
                run.Id,
                It.IsAny<string>(),
                JobRunActionStatus.Succeeded,
                0,
                null,
                It.IsAny<string>(),
                It.IsAny<string>(),
                CancellationToken.None))
            .Callback<string, string, JobRunActionStatus, int?, string?, string?, string?, CancellationToken>(
                (_, actionId, _, _, _, _, _, _) => completed.Add(actionId))
            .Returns(Task.CompletedTask);
        store
            .Setup(candidate => candidate.CompleteIdleRunAsync(run.Id, CancellationToken.None))
            .ReturnsAsync(JobRunStatus.Succeeded);

        var scriptService = PreparedScripts();
        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli
            .Setup(candidate => candidate.RunAsync(
                It.IsAny<CliRequest>(),
                It.IsAny<Func<CliOutputLine, ValueTask>>(),
                CancellationToken.None))
            .ReturnsAsync((CliRequest request, Func<CliOutputLine, ValueTask>? _, CancellationToken _) =>
                new CliResult(
                    0,
                    false,
                    false,
                    $"stdout:{request.Arguments[0]}",
                    string.Empty,
                    TimeSpan.FromMilliseconds(5),
                    request.Executable));

        using var services = BuildServices(store, scriptService, cli);
        var exitCode = await JobRunner.RunAsync(
            new ParsedArgs { JobRunId = run.Id, WorkDir = run.ProjectPath },
            services);

        Assert.Equal(0, exitCode);
        Assert.Equal([first.Id, second.Id], started);
        Assert.Equal([first.Id, second.Id], completed);
        store.Verify(candidate => candidate.CompleteRunActionAsync(
            run.Id,
            first.Id,
            JobRunActionStatus.Succeeded,
            0,
            null,
            "stdout:scripts/first.py",
            string.Empty,
            CancellationToken.None), Times.Once);
        store.Verify(candidate => candidate.CompleteRunActionAsync(
            run.Id,
            second.Id,
            JobRunActionStatus.Succeeded,
            0,
            null,
            "stdout:scripts/second.py",
            string.Empty,
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFirstFailedAction()
    {
        var first = ScriptAction("first", position: 0, "scripts/first.py");
        var second = ScriptAction("second", position: 1, "scripts/second.py");
        var run = Run([first, second]);
        var store = StoreFor(run);
        store
            .Setup(candidate => candidate.StartRunActionAsync(run.Id, first.Id, CancellationToken.None))
            .ReturnsAsync(true);
        store
            .Setup(candidate => candidate.CompleteRunActionAsync(
                run.Id,
                first.Id,
                JobRunActionStatus.Failed,
                9,
                It.Is<string>(message => message.Contains("exited with code 9", StringComparison.Ordinal)),
                "partial output",
                "failure output",
                CancellationToken.None))
            .Returns(Task.CompletedTask);
        store
            .Setup(candidate => candidate.CompleteRunAsync(
                run.Id,
                JobRunStatus.Failed,
                9,
                It.Is<string>(message => message.Contains("exited with code 9", StringComparison.Ordinal)),
                CancellationToken.None))
            .Returns(Task.CompletedTask);

        var scriptService = PreparedScripts();
        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli
            .Setup(candidate => candidate.RunAsync(
                It.Is<CliRequest>(request => request.Arguments[0] == first.ScriptPath),
                It.IsAny<Func<CliOutputLine, ValueTask>>(),
                CancellationToken.None))
            .ReturnsAsync(new CliResult(
                9,
                false,
                false,
                "partial output",
                "failure output",
                TimeSpan.FromMilliseconds(5),
                "python-test"));

        using var services = BuildServices(store, scriptService, cli);
        var exitCode = await JobRunner.RunAsync(
            new ParsedArgs { JobRunId = run.Id, WorkDir = run.ProjectPath },
            services);

        Assert.Equal(JobRunOutcome.ToExitCode(JobRunStatus.Failed), exitCode);
        store.Verify(candidate => candidate.StartRunActionAsync(
            run.Id,
            second.Id,
            It.IsAny<CancellationToken>()), Times.Never);
        scriptService.Verify(candidate => candidate.PrepareAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<JobRunActionRecord>(action => action.Id == second.Id),
            It.IsAny<CancellationToken>()), Times.Never);
        cli.Verify(candidate => candidate.RunAsync(
            It.IsAny<CliRequest>(),
            It.IsAny<Func<CliOutputLine, ValueTask>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_RecordsSuccessThroughTheCancelAwareStorePath()
    {
        // The cancel flag is read once before completion. A Stop that lands after that read must
        // still win, which only the store's atomic Succeeded-or-Cancelled write can guarantee —
        // so success never goes through the plain CompleteRunAsync, and the exit code follows
        // whatever that write actually recorded.
        var only = ScriptAction("only", position: 0, "scripts/only.py");
        var run = Run([only]);
        var store = StoreFor(run);
        store
            .Setup(candidate => candidate.StartRunActionAsync(run.Id, only.Id, CancellationToken.None))
            .ReturnsAsync(true);
        store
            .Setup(candidate => candidate.CompleteRunActionAsync(
                run.Id,
                only.Id,
                JobRunActionStatus.Succeeded,
                0,
                null,
                It.IsAny<string>(),
                It.IsAny<string>(),
                CancellationToken.None))
            .Returns(Task.CompletedTask);
        store
            .Setup(candidate => candidate.CompleteIdleRunAsync(run.Id, CancellationToken.None))
            .ReturnsAsync(JobRunStatus.Cancelled);

        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli
            .Setup(candidate => candidate.RunAsync(
                It.IsAny<CliRequest>(),
                It.IsAny<Func<CliOutputLine, ValueTask>>(),
                CancellationToken.None))
            .ReturnsAsync(new CliResult(0, false, false, string.Empty, string.Empty, TimeSpan.Zero, "python-test"));

        using var services = BuildServices(store, PreparedScripts(), cli);
        var exitCode = await JobRunner.RunAsync(
            new ParsedArgs { JobRunId = run.Id, WorkDir = run.ProjectPath },
            services);

        Assert.Equal(JobRunOutcome.ToExitCode(JobRunStatus.Cancelled), exitCode);
        store.Verify(candidate => candidate.CompleteRunAsync(
            It.IsAny<string>(),
            It.IsAny<JobRunStatus>(),
            It.IsAny<int?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, false, JobRunStatus.Succeeded, 0, false)]
    [InlineData(9, false, JobRunStatus.Failed, 9, false)]
    [InlineData(0, true, JobRunStatus.TimedOut, 2, false)]
    [InlineData(0, false, JobRunStatus.Cancelled, 3, false)]
    [InlineData(0, false, JobRunStatus.Succeeded, 0, true)]
    public async Task NativeScripts_RecordReplayTiming_LinkBoardCard_AndEndWithActualOutcome(
        int scriptExitCode, bool timedOut, JobRunStatus outcome, int sessionExitCode, bool finalWriteFails)
    {
        var root = Path.Combine(Path.GetTempPath(), $"job-script-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stateConnection = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            var repository = new Repository(stateConnection);
            var boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", stateConnection);
            var ct = TestContext.Current.CancellationToken;
            await boards.EnsureDefaultColumnsAsync(root, ct);
            var card = await boards.CreateCardAsync(root, new(null, "Run script", "", null, "medium", null, [], false), ct);
            var action = ScriptAction("first", 0, "script.py");
            var run = Run([action]) with { ProjectPath = root, TriggerKind = JobTriggerKind.BoardLane, TriggerKey = $"board-lane:{card.Key}:lane:event" };
            var store = StoreFor(run);
            store.Setup(candidate => candidate.StartRunActionAsync(run.Id, action.Id, CancellationToken.None)).ReturnsAsync(true);
            store.Setup(candidate => candidate.CompleteRunActionAsync(run.Id, action.Id, It.IsAny<JobRunActionStatus>(), It.IsAny<int?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), CancellationToken.None)).Returns(Task.CompletedTask);
            store.Setup(candidate => candidate.CompleteRunAsync(run.Id, It.IsAny<JobRunStatus>(), It.IsAny<int?>(), It.IsAny<string?>(), CancellationToken.None)).Returns(Task.CompletedTask);
            store.Setup(candidate => candidate.CompleteIdleRunAsync(run.Id, CancellationToken.None)).ReturnsAsync(outcome);
            if (finalWriteFails)
                store.Setup(candidate => candidate.CompleteIdleRunAsync(run.Id, CancellationToken.None)).ThrowsAsync(new InvalidOperationException("Final run write failed"));
            string? sessionId = null;
            store.Setup(candidate => candidate.LinkRunTerminalSessionAsync(run.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, id, _) => sessionId = id).Returns(Task.CompletedTask);
            var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
            cli.Setup(candidate => candidate.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>>(), CancellationToken.None))
                .Returns(async (CliRequest _, Func<CliOutputLine, ValueTask> emit, CancellationToken _) =>
                {
                    await emit(new(false, "real stdout", TimeSpan.FromMilliseconds(10)));
                    await emit(new(true, "real stderr", TimeSpan.FromMilliseconds(70)));
                    return new CliResult(scriptExitCode, timedOut, false, "real stdout", "real stderr", TimeSpan.FromMilliseconds(80), "test");
                });
            using var services = BuildServices(store, PreparedScripts(), cli, repository, boards);

            var execution = JobRunner.RunAsync(new ParsedArgs { JobRunId = run.Id, WorkDir = root }, services);
            if (finalWriteFails) await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
            else await execution;

            Assert.NotNull(sessionId);
            var session = await repository.GetSessionByIdAsync(sessionId, ct);
            Assert.NotNull(session);
            Assert.NotNull(session.EndedUTC);
            Assert.Equal(sessionExitCode, session.ExitCode);
            var logs = await repository.GetTerminalSessionLogsAsync(sessionId, ct);
            var stdout = Assert.Single(logs, line => System.Text.Encoding.UTF8.GetString(line.Data) == "real stdout\r\n");
            var stderr = Assert.Single(logs, line => System.Text.Encoding.UTF8.GetString(line.Data) == "real stderr\r\n");
            Assert.Equal(TimeSpan.FromMilliseconds(60), stderr.TimestampUtc - stdout.TimestampUtc);
            Assert.True(stderr.Sequence > stdout.Sequence);
            var raw = await repository.GetSessionWithLogsAsync(sessionId, ct);
            Assert.True(Assert.Single(raw!.Logs, line => line.Content == Convert.ToBase64String("real stderr\r\n"u8)).IsError);
            Assert.False(Assert.Single(raw.Logs, line => line.Content == Convert.ToBase64String("real stdout\r\n"u8)).IsError);
            Assert.Equal(sessionId, Assert.Single((await boards.GetCardDetailAsync(root, card.Id, ct))!.Sessions).SessionId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TerminalTabScripts_UseTheirExistingOuterRecording()
    {
        var action = ScriptAction("script", 0, "script.py");
        var run = Run([action]) with { LaunchInTerminalTab = true, TerminalSessionId = "outer-session" };
        var store = StoreFor(run);
        store.Setup(candidate => candidate.StartRunActionAsync(run.Id, action.Id, CancellationToken.None)).ReturnsAsync(true);
        store.Setup(candidate => candidate.CompleteRunActionAsync(run.Id, action.Id, It.IsAny<JobRunActionStatus>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), CancellationToken.None)).Returns(Task.CompletedTask);
        store.Setup(candidate => candidate.CompleteIdleRunAsync(run.Id, CancellationToken.None)).ReturnsAsync(JobRunStatus.Succeeded);
        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli.Setup(candidate => candidate.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>>(), CancellationToken.None))
            .ReturnsAsync(new CliResult(0, false, false, "output", "", TimeSpan.Zero, "test"));
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        using var services = BuildServices(store, PreparedScripts(), cli, repository.Object);

        Assert.Equal(0, await JobRunner.RunAsync(new ParsedArgs { JobRunId = run.Id, WorkDir = run.ProjectPath }, services));

        repository.VerifyNoOtherCalls();
        store.Verify(candidate => candidate.LinkRunTerminalSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServiceProvider BuildServices(
        Mock<IJobStore> store,
        Mock<IAutomationScriptService> scriptService,
        Mock<ICliWrapper> cli,
        IRepository? repository = null,
        IBoardStore? boards = null)
    {
        var automationConsumer = new Mock<IAutomationConsumer>(MockBehavior.Strict);
        automationConsumer.SetupGet(candidate => candidate.IdleShutdownToken).Returns(CancellationToken.None);
        automationConsumer.SetupGet(candidate => candidate.IdleShutdownRequested).Returns(false);

        var services = new ServiceCollection()
            .AddSingleton(store.Object)
            .AddSingleton(scriptService.Object)
            .AddSingleton(cli.Object)
            .AddSingleton(automationConsumer.Object)
            .AddSingleton(repository ?? Mock.Of<IRepository>());
        if (boards is not null) services.AddSingleton(boards);
        return services.BuildServiceProvider();
    }

    private static Mock<IJobStore> StoreFor(JobRunRecord run)
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(candidate => candidate.GetRunAsync(run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        store
            .Setup(candidate => candidate.StartRunAsync(run.Id, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        store
            .Setup(candidate => candidate.IsCancelRequestedAsync(run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        store.Setup(candidate => candidate.LinkRunTerminalSessionAsync(run.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return store;
    }

    private static Mock<IAutomationScriptService> PreparedScripts()
    {
        var service = new Mock<IAutomationScriptService>(MockBehavior.Strict);
        service
            .Setup(candidate => candidate.PrepareAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<JobRunActionRecord>(),
                CancellationToken.None))
            .ReturnsAsync((string root, string _, JobRunActionRecord action, CancellationToken _) =>
                new PreparedAutomationScript(
                    "python-test",
                    [action.ScriptPath!],
                    root,
                    new Dictionary<string, string?>()));
        return service;
    }

    private static JobRunRecord Run(IReadOnlyList<JobRunActionRecord> actions) => new(
        "run-1",
        7,
        JobTriggerKind.Manual,
        "manual:run-1",
        JobRunStatus.Queued,
        "Script workflow",
        AppContext.BaseDirectory,
        LLM.NotSet,
        null,
        null,
        null,
        null,
        DateTime.UtcNow,
        null,
        null,
        null,
        null,
        false,
        null,
        false,
        actions);

    private static JobRunActionRecord ScriptAction(string id, int position, string path) => new(
        id,
        "run-1",
        id,
        position,
        JobActionKind.Script,
        JobRunActionStatus.Pending,
        null,
        null,
        LLM.NotSet,
        path,
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
}
