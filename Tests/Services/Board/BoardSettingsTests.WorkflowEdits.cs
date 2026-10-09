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
    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task WorkflowSettingsUpgradePreservesSnapshotsAndLegacyWriters(string operation)
    {
        var (_, lane, other, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var retiredCard = await Card(lane);
        await _boards.MoveCardAsync(_root, retiredCard.Id, other, null, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var run = Assert.Single(await Tick(due));
        await _jobs.StartRunAsync(run, 1234, Ct);
        var before = await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct);
        // Restore the v1 schema to verify the automatic forward migration on a populated file.
        await ExecuteSql("""
            DELETE FROM SchemaMigrations WHERE Component = 'board-lane-workflow' AND Version = 2;
            CREATE TRIGGER BoardLaneAutomations_Workflow_UPDATE AFTER UPDATE ON BoardLaneAutomations BEGIN
                UPDATE BoardLaneWorkflows SET Current = 0 WHERE ColumnId = OLD.ColumnId AND Current = 1;
            END;
            CREATE TRIGGER BoardLaneAutomations_Workflow_DELETE AFTER DELETE ON BoardLaneAutomations BEGIN
                UPDATE BoardLaneWorkflows SET Current = 0 WHERE ColumnId = OLD.ColumnId AND Current = 1;
            END;
            """);
        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        _ = new BoardStore(_connectionString, _stateConnectionString); // Reopening is idempotent.
        Assert.Equal(before, await upgraded.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        Assert.All(await upgraded.GetLaneAutomationStatusesAsync(_root, retiredCard.Id, Ct), s => Assert.False(s.IsCurrent));

        if (operation == "update")
            await ExecuteSql($"""
                UPDATE BoardLaneAutomations SET Revision = Revision + 1 WHERE ColumnId = '{lane}';
                DELETE FROM BoardPendingAutomations WHERE ColumnId = '{lane}';
                """);
        else
            await ExecuteSql($"DELETE FROM BoardLaneAutomations WHERE ColumnId = '{lane}';");

        var steps = Assert.Single(await upgraded.GetLaneWorkflowsAsync(_root, lane, Ct)).Steps;
        Assert.Equal(before[0].WorkflowId, steps[0].WorkflowId);
        Assert.Equal("Running", steps[0].StepStatus);
        Assert.True(steps[0].CanSkip);
        Assert.Equal("Cancelled", steps[1].StepStatus);
        Assert.Empty(await upgraded.GetPendingLaneAutomationsAsync(_root, card.Id, Ct));
        await new BoardCardAutomationService(upgraded, _jobs, Mock.Of<IJobService>())
            .SkipAsync(_root, card.Id, first.Id, steps[0].EventKey, Ct);
        Assert.True(await _jobs.IsCancelRequestedAsync(run, Ct));
        await _jobs.CompleteRunAsync(run, JobRunStatus.Cancelled, 1, "Stopped", Ct);
        Assert.Empty(await Tick(due + 1));
        Assert.Empty(await _jobs.GetRunsAsync(second.Id, cancellationToken: Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleSkipReceiptDoesNotCancelCommittedRun(bool running)
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var run = Assert.Single(await Tick(await Due(card.Id)));
        if (running) await _jobs.StartRunAsync(run, 1234, Ct);
        var entry = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        var racing = new Mock<IBoardStore>(MockBehavior.Strict);
        racing.Setup(s => s.FindCardAsync(_root, card.Id, Ct)).ReturnsAsync(card);
        racing.Setup(s => s.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Returns(async () =>
        {
            await _boards.MoveCardAsync(_root, card.Id, other, null, Ct);
            await _boards.MoveCardAsync(_root, card.Id, lane, null, Ct);
            return new[] { entry };
        });
        racing.Setup(s => s.ReportLaneStepAsync(_root, card.Id, It.IsAny<BoardLaneStepReport>(), It.IsAny<BoardAuthor>(), Ct))
            .Returns<string, string, BoardLaneStepReport, BoardAuthor, CancellationToken>(_boards.ReportLaneStepAsync);

        var service = new BoardCardAutomationService(racing.Object, _jobs, Mock.Of<IJobService>());
        await Assert.ThrowsAsync<BoardConflictException>(() => service.SkipAsync(_root, card.Id, job.Id, entry.EventKey, Ct));

        Assert.False(await _jobs.IsCancelRequestedAsync(run, Ct));
        Assert.Equal(running ? JobRunStatus.Running : JobRunStatus.Queued, (await _jobs.GetRunAsync(run, Ct))!.Status);
        var current = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Single(s => s.IsCurrent);
        Assert.NotEqual(entry.EventKey, current.EventKey);
        Assert.Equal("Waiting", current.StepStatus);
        Assert.DoesNotContain((await _boards.GetCardDetailAsync(_root, card.Id, Ct))!.Comments,
            c => c.Body.Contains("User skipped"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommittedSkipReceiptDoesNotCancelRunAndCanBeRetried(bool running)
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var run = Assert.Single(await Tick(await Due(card.Id)));
        if (running) await _jobs.StartRunAsync(run, 1234, Ct);
        var entry = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        await ExecuteSql("""
            CREATE TRIGGER RejectCommittedSkipReceipt BEFORE INSERT ON BoardComments
            BEGIN SELECT RAISE(ABORT, 'receipt write failed'); END;
            """);
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        await Assert.ThrowsAsync<SqliteException>(() => service.SkipAsync(_root, card.Id, job.Id, entry.EventKey, Ct));
        Assert.False(await _jobs.IsCancelRequestedAsync(run, Ct));
        Assert.Equal(entry.StepStatus, Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).StepStatus);
        await ExecuteSql("DROP TRIGGER RejectCommittedSkipReceipt;");
        await service.SkipAsync(_root, card.Id, job.Id, entry.EventKey, Ct);
        Assert.Equal(running ? "Stopping" : "Skipped",
            Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).StepStatus);
    }
}
