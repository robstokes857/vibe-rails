using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobRunnerWorkflowTests
{
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

    private static ServiceProvider BuildServices(
        Mock<IJobStore> store,
        Mock<IAutomationScriptService> scriptService,
        Mock<ICliWrapper> cli)
    {
        var automationConsumer = new Mock<IAutomationConsumer>(MockBehavior.Strict);
        automationConsumer.SetupGet(candidate => candidate.IdleShutdownToken).Returns(CancellationToken.None);
        automationConsumer.SetupGet(candidate => candidate.IdleShutdownRequested).Returns(false);

        return new ServiceCollection()
            .AddSingleton(store.Object)
            .AddSingleton(scriptService.Object)
            .AddSingleton(cli.Object)
            .AddSingleton(automationConsumer.Object)
            .BuildServiceProvider();
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
