using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Fact]
    public async Task LanePollReturnsCurrentWorkflowProgressAfterSkip()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        var laneService = new BoardAutomationService(_boards, _jobs);
        var initial = Assert.Single((await laneService.GetRunningAgentsAsync(_root, lane, Ct))!.Workflows!);
        Assert.Equal(card.Id, initial.CardId);
        Assert.Equal(new[] { first.Id, second.Id }, initial.Steps.Select(s => s.JobId));
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        await service.SkipAsync(_root, card.Id, first.Id, initial.Steps[0].EventKey, Ct);
        var updated = Assert.Single((await laneService.GetRunningAgentsAsync(_root, lane, Ct))!.Workflows!);
        Assert.Equal("Skipped", updated.Steps[0].StepStatus);
        Assert.Equal("Waiting", updated.Steps[1].StepStatus);
        Assert.Null(await laneService.GetRunningAgentsAsync(_root + "-foreign", lane, Ct));
    }

    [Fact]
    public async Task SkipDuringDispatchPreparationCannotStartTheSkippedStepAfterItsSuccessor()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var entry = Assert.Single(await _boards.GetDueLaneAutomationsAsync(DateTimeOffset.FromUnixTimeMilliseconds(due).UtcDateTime, Ct));
        var delivery = new Mock<IBoardStore>(MockBehavior.Strict);
        delivery.Setup(s => s.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { entry });
        delivery.Setup(s => s.IsLaneAutomationCurrentAsync(entry, It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, CancellationToken>(_boards.IsLaneAutomationCurrentAsync);
        delivery.Setup(s => s.GetLaneAutomationBlockReasonAsync(entry, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var reason = await _boards.GetLaneAutomationBlockReasonAsync(entry, Ct);
                var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
                await service.SkipAsync(_root, card.Id, first.Id, entry.EventKey, Ct);
                var successor = Assert.Single(await Tick(due));
                Assert.Equal(second.Id, (await _jobs.GetRunAsync(successor, Ct))!.JobId);
                return reason;
            });
        delivery.Setup(s => s.RecordLaneAutomationDispatchAsync(entry, It.IsAny<BoardLaneAutomationDispatch>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, BoardLaneAutomationDispatch, DateTime, CancellationToken>(_boards.RecordLaneAutomationDispatchAsync);
        Assert.Empty(await Tick(due, new JobStore(_stateConnectionString, delivery.Object)));
        Assert.Empty(await _jobs.GetRunsAsync(first.Id, cancellationToken: Ct));
        Assert.Single(await _jobs.GetRunsAsync(second.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task RunningStepSkipWaitsForItsProcessToStop()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var run = Assert.Single(await Tick(due));
        await _jobs.StartRunAsync(run, 1234, Ct);
        var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0];
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        var skipped = await service.SkipAsync(_root, card.Id, first.Id, entry.EventKey, Ct);
        Assert.Equal("Stopping", skipped!.LaneEntries![0].StepStatus);
        Assert.True(await _jobs.IsCancelRequestedAsync(run, Ct));
        Assert.Empty(await Tick(due + 1));
        await _jobs.CompleteRunAsync(run, JobRunStatus.Cancelled, 1, "Stopped", Ct);
        Assert.Equal(second.Id, (await _jobs.GetRunAsync(Assert.Single(await Tick(due + 2)), Ct))!.JobId);
        Assert.Equal("Skipped", (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0].StepStatus);
    }

    [Fact]
    public async Task WorkflowUpgradeKeepsExistingPendingEntriesOrderedWithoutBackfill()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        await ExecuteSql("""
            DROP TRIGGER BoardCards_Workflow_Insert;
            DROP TRIGGER BoardCards_Workflow_Move;
            DROP TRIGGER BoardPendingAutomations_Workflow;
            DROP TRIGGER BoardPendingAdditionalAutomations_Workflow;
            DROP TRIGGER BoardLaneAutomations_Workflow_UPDATE;
            DROP TRIGGER BoardLaneAutomations_Workflow_DELETE;
            DROP TABLE BoardLaneStepReports;
            DROP TABLE BoardLaneWorkflowSteps;
            DROP TABLE BoardLaneWorkflows;
            DELETE FROM SchemaMigrations WHERE Component = 'board-lane-workflow';
            """);
        var boards = new BoardStore(_connectionString, _stateConnectionString);
        var jobs = new JobStore(_stateConnectionString, boards);
        Assert.Equal(due, await Due(card.Id));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM BoardLaneWorkflows";
        Assert.Equal(0L, await query.ExecuteScalarAsync(Ct));
        var run = Assert.Single(await Tick(due, jobs));
        Assert.Equal(first.Id, (await jobs.GetRunAsync(run, Ct))!.JobId);
        Assert.Empty(await Tick(due + 1, jobs));
        await jobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Equal(second.Id, (await jobs.GetRunAsync(Assert.Single(await Tick(due + 2, jobs)), Ct))!.JobId);
    }
}
