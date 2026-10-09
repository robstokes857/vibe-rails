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
    public async Task LegacyWorkflowListsCurrentStepsBeforeUnselectedSteps()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("Selected");
        var second = await Job("Unselected");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        // Model a legacy entry with no workflow snapshot and a selection changed by an old writer.
        await ExecuteSql($"""
            DELETE FROM BoardLaneWorkflowSteps WHERE WorkflowId IN (SELECT Id FROM BoardLaneWorkflows WHERE CardId = '{card.Id}');
            DELETE FROM BoardLaneWorkflows WHERE CardId = '{card.Id}';
            DELETE FROM BoardLaneAdditionalAutomations WHERE ColumnId = '{lane}';
            """);

        var rows = await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct);

        Assert.Equal(new[] { first.Id, second.Id }, rows.Select(row => row.JobId));
        Assert.Equal(rows[0].WorkflowId, rows[1].WorkflowId);
        Assert.True(rows[0].IsCurrent);
        Assert.False(rows[1].IsCurrent);
        Assert.Equal(-1, rows[1].Position);
    }

    [Fact]
    public async Task WorkflowStatusOrdersByCurrentDueWorkflowAndPosition()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        var card = await Card(lane);
        await ExecuteSql($"""
            INSERT INTO BoardLaneWorkflows (Id, CardId, ColumnId, CreatedUnixMs, Current) VALUES
                ('z-current', '{card.Id}', '{lane}', 1000, 1),
                ('newest', '{card.Id}', '{lane}', 3000, 0),
                ('a-history', '{card.Id}', '{lane}', 2000, 0),
                ('b-history', '{card.Id}', '{lane}', 2000, 0),
                ('oldest', '{card.Id}', '{lane}', 1000, 0);
            INSERT INTO BoardLaneWorkflowSteps (WorkflowId, JobId, Position, EventKey) VALUES
                ('z-current', {first.Id}, 0, 'current'),
                ('newest', {first.Id}, 0, 'newest'),
                ('a-history', {first.Id}, 0, 'd'),
                ('a-history', {second.Id}, 1, 'b'),
                ('b-history', {first.Id}, 0, 'c'),
                ('b-history', {second.Id}, 1, 'a'),
                ('oldest', {first.Id}, 0, 'oldest');
            INSERT INTO BoardLaneAutomationDispatch (EventKey, JobId, CardId, ColumnId, DueUnixMs, Status, Reason)
                SELECT s.EventKey, s.JobId, w.CardId, w.ColumnId, w.CreatedUnixMs, 'Succeeded', ''
                FROM BoardLaneWorkflows w JOIN BoardLaneWorkflowSteps s ON s.WorkflowId = w.Id
                WHERE w.CardId = '{card.Id}';
            """);

        var rows = await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct);

        Assert.Equal(new[] { "current", "newest", "d", "b", "c", "a", "oldest" }, rows.Select(row => row.EventKey));
        Assert.True(rows[0].IsCurrent);
        Assert.All(rows.Skip(1), row => Assert.False(row.IsCurrent));
    }

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
            DROP TRIGGER IF EXISTS BoardLaneAutomations_Workflow_UPDATE;
            DROP TRIGGER IF EXISTS BoardLaneAutomations_Workflow_DELETE;
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
