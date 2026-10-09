using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    private async Task SetDue(BoardCardRecord card, long due) => await ExecuteSql($"""
        UPDATE BoardPendingAutomations SET DueUnixMs = {due} WHERE CardId = '{card.Id}';
        UPDATE BoardPendingAdditionalAutomations SET DueUnixMs = {due} WHERE CardId = '{card.Id}';
        """);

    [Fact]
    public async Task AdditiveUpgradePreservesPendingEntriesAndRuns_WithoutBackfill()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var run = (await _jobs.EnqueueManualRunAsync(job.Id, Ct))!;
        await ExecuteSql("""
            DROP TRIGGER BoardPendingAutomations_RecordCancellation;
            DROP TRIGGER BoardPendingAdditionalAutomations_RecordCancellation;
            DROP TABLE BoardLaneAutomationDispatch;
            DELETE FROM SchemaMigrations WHERE Component = 'board-lane-dispatch';
            """);
        var roots = await Task.WhenAll(Task.Run(ReopenJobs, Ct), Task.Run(ReopenJobs, Ct));
        Assert.Equal(due, await Due(card.Id));
        Assert.Equal(run, Assert.Single(await roots[0].GetRunsAsync(job.Id, cancellationToken: Ct)).Id);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BoardLaneAutomationDispatch";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
        Assert.Empty(await Tick(due, roots[1]));
        Assert.Equal(due, await Due(card.Id));
    }

    [Fact]
    public async Task EventReadBeforeDeparture_IsRecheckedBeforeCreatingARun()
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var now = DateTime.UtcNow.AddMinutes(2);
        var stale = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await _boards.MoveCardAsync(_root, card.Id, other, null, Ct);
        var delivery = new Mock<IBoardStore>(MockBehavior.Strict);
        delivery.Setup(s => s.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { stale });
        delivery.Setup(s => s.IsLaneAutomationCurrentAsync(stale, It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, CancellationToken>(_boards.IsLaneAutomationCurrentAsync);
        delivery.Setup(s => s.RecordLaneAutomationDispatchAsync(stale, It.IsAny<BoardLaneAutomationDispatch>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, BoardLaneAutomationDispatch, DateTime, CancellationToken>(_boards.RecordLaneAutomationDispatchAsync);
        Assert.Empty(await new JobStore(_stateConnectionString, delivery.Object).EnqueueDueSchedulesAsync(now, Ct));
        Assert.Empty(await _jobs.GetRunsAsync(job.Id, cancellationToken: Ct));
        Assert.Equal("Cancelled", Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Status);
    }

    [Theory]
    [InlineData(JobRunStatus.Succeeded)]
    [InlineData(JobRunStatus.Failed)]
    [InlineData(JobRunStatus.Cancelled)]
    public async Task SeparateCardsWaitInOrder_AfterBusyWorkerEnds_AndAcrossRootRestart(JobRunStatus outcome)
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var busy = (await _jobs.EnqueueManualRunAsync(job.Id, Ct))!;
        Assert.True(await _jobs.StartRunAsync(busy, 1234, Ct)); // Fake worker; never launch a process.
        var first = await Card(lane);
        var second = await Card(lane);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await SetDue(first, now - 2);
        await SetDue(second, now - 1);
        Assert.Empty(await Tick(now));
        Assert.Equal(2, (await _boards.GetPendingLaneAutomationsAsync(_root, first.Id, Ct)).Count
            + (await _boards.GetPendingLaneAutomationsAsync(_root, second.Id, Ct)).Count);
        var waiting = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, second.Id, Ct));
        Assert.Equal("Waiting", waiting.Status);
        Assert.Contains("busy", waiting.Reason);
        Assert.Null(waiting.RunId);
        await _jobs.CompleteRunAsync(busy, outcome, 1, "Fake worker ended", Ct);

        var firstRun = Assert.Single(await Tick(now + 1, ReopenJobs()));
        Assert.Contains(first.Key, (await _jobs.GetRunAsync(firstRun, Ct))!.TriggerKey);
        Assert.Equal("Queued", Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, first.Id, Ct)).Status);
        Assert.Empty(await Tick(now + 2));
        Assert.True(await _jobs.StartRunAsync(firstRun, 1234, Ct));
        Assert.Equal("Running", Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, first.Id, Ct)).Status);
        await _jobs.CompleteRunAsync(firstRun, JobRunStatus.Failed, 1, "Fake launch failure", Ct);
        var failed = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, first.Id, Ct));
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("Fake launch failure", failed.Reason);
        var secondRun = Assert.Single(await Tick(now + 3, ReopenJobs()));
        Assert.Contains(second.Key, (await _jobs.GetRunAsync(secondRun, Ct))!.TriggerKey);
        Assert.Equal(3, (await _jobs.GetRunsAsync(job.Id, 200, Ct)).Count);
    }

    [Fact]
    public async Task ManyCardsForOneBusyJob_DoNotHideOtherJobs_AndEventuallyEachRunsOnce()
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        var independent = await Job("Independent");
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, other, [independent.Id], 0, Ct);
        var busy = (await _jobs.EnqueueManualRunAsync(job.Id, Ct))!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cards = new List<BoardCardRecord>();
        for (var i = 0; i < 105; i++)
        {
            var card = await Card(lane);
            await SetDue(card, now - 200 + i);
            cards.Add(card);
        }
        var freeCard = await Card(other);
        await SetDue(freeCard, now - 1);
        var freeRun = Assert.Single(await Tick(now));
        Assert.Equal(independent.Id, (await _jobs.GetRunAsync(freeRun, Ct))!.JobId);
        await _jobs.CompleteRunAsync(busy, JobRunStatus.Succeeded, 0, null, Ct);
        for (var i = 0; i < cards.Count; i++)
        {
            var runId = Assert.Single(await Tick(now + i + 1, i == 50 ? ReopenJobs() : null));
            Assert.Contains(cards[i].Key, (await _jobs.GetRunAsync(runId, Ct))!.TriggerKey);
            await _jobs.CompleteRunAsync(runId, JobRunStatus.Succeeded, 0, null, Ct);
        }
        Assert.Empty(await Tick(now + 1000));
        Assert.Equal(106, (await _jobs.GetRunsAsync(job.Id, 200, Ct)).Count);
    }

    [Fact]
    public async Task MoreThanOneBatchOfBusyJobs_PreservesSequentialOrderAcrossRestart()
    {
        var (_, lane, _, _) = await Lanes();
        var jobs = new List<long>();
        for (var i = 0; i < 103; i++)
        {
            var job = await Job($"Job {i}");
            jobs.Add(job.Id);
            await _jobs.EnqueueManualRunAsync(job.Id, Ct);
        }
        await _boards.SaveLaneAutomationAsync(_root, lane, jobs, 0, Ct);
        var card = await Card(lane);
        var now = await Due(card.Id);
        var first = Assert.Single(await _boards.GetDueLaneAutomationsAsync(DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime, Ct));
        Assert.Equal(jobs[0], first.JobId);
        Assert.Empty(await Tick(now));
        var next = Assert.Single(await new BoardStore(_connectionString, _stateConnectionString)
            .GetDueLaneAutomationsAsync(DateTimeOffset.FromUnixTimeMilliseconds(now + 1).UtcDateTime, Ct));
        Assert.Equal(first.EventKey, next.EventKey);
        Assert.Equal(103, (await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct)).Count);
    }

    [Fact]
    public async Task TwoRootsWithDuplicateDelivery_CommitOnlyOneRunAndOneWorkerClaim()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(await Due(card.Id)).UtcDateTime;
        var entry = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        var delivery = new Mock<IBoardStore>(MockBehavior.Strict);
        delivery.Setup(s => s.GetDueLaneAutomationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { entry, entry });
        delivery.Setup(s => s.GetLaneAutomationBlockReasonAsync(It.IsAny<BoardLaneAutomationEvent>(), It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, CancellationToken>(_boards.GetLaneAutomationBlockReasonAsync);
        delivery.Setup(s => s.GetLaneAutomationStatusesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>(_boards.GetLaneAutomationStatusesAsync);
        delivery.Setup(s => s.IsLaneAutomationCurrentAsync(entry, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        delivery.Setup(s => s.RecordLaneAutomationDispatchAsync(entry, It.IsAny<BoardLaneAutomationDispatch>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<BoardLaneAutomationEvent, BoardLaneAutomationDispatch, DateTime, CancellationToken>(_boards.RecordLaneAutomationDispatchAsync);
        var rootA = new JobStore(_stateConnectionString, delivery.Object);
        var rootB = new JobStore(_stateConnectionString, delivery.Object);
        var results = await Task.WhenAll(Task.Run(() => rootA.EnqueueDueSchedulesAsync(now, Ct), Ct),
            Task.Run(() => rootB.EnqueueDueSchedulesAsync(now, Ct), Ct));
        var run = Assert.Single(results.SelectMany(x => x));
        var starts = await Task.WhenAll(rootA.StartRunAsync(run, 101, Ct), rootB.StartRunAsync(run, 102, Ct));
        Assert.Single(starts, started => started);
        Assert.Single(await _jobs.GetRunsAsync(job.Id, cancellationToken: Ct));
        await _jobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Empty(await rootB.EnqueueDueSchedulesAsync(now, Ct));
        Assert.Single(await _jobs.GetRunsAsync(job.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task StaleBusyAndAcknowledgement_DoNotReviveCancelledEntryOrRemoveReentry()
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var now = DateTime.UtcNow.AddMinutes(2);
        var original = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await _boards.RecordLaneAutomationDispatchAsync(original, new("Waiting", "Busy"), now, Ct);
        await _boards.MoveCardAsync(_root, card.Id, other, null, Ct);
        Assert.False(await _boards.IsLaneAutomationCurrentAsync(original, Ct));
        await _boards.MoveCardAsync(_root, card.Id, lane, null, Ct);
        var next = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await _boards.RecordLaneAutomationDispatchAsync(original, new("Waiting", "Stale busy"), now, Ct);
        await _boards.RecordLaneAutomationDispatchAsync(original, new("Cancelled", "Stale acknowledgement"), now, Ct);
        var states = await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct);
        Assert.Equal("Cancelled", states.Single(s => s.EventKey == original.EventKey).Status);
        Assert.Contains("left the destination lane", states.Single(s => s.EventKey == original.EventKey).Reason);
        Assert.Equal(next.EventKey, Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct)).EventKey);
        Assert.Single(states, s => s.Status == "Waiting");
    }

    [Theory]
    [InlineData("disabled", "disabled", "Skipped")]
    [InlineData("deleted", "deleted", "Skipped")]
    [InlineData("missing", "no longer exists", "Skipped")]
    [InlineData("project", "another project", "Failed")]
    [InlineData("actions", "no actions", "Failed")]
    public async Task UnavailableJobIsTerminalWithExplicitReason(string change, string reason, string expectedStatus)
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        await using var state = new SqliteConnection(_stateConnectionString);
        await state.OpenAsync(Ct);
        await using var command = state.CreateCommand();
        command.CommandText = change switch {
            "disabled" => "UPDATE Jobs SET Enabled = 0 WHERE Id = $id;",
            "deleted" => "UPDATE Jobs SET DeletedUTC = '2026-01-01T00:00:00Z' WHERE Id = $id;",
            "missing" => "DELETE FROM Jobs WHERE Id = $id;",
            "project" => "UPDATE Jobs SET ProjectPath = 'other-project' WHERE Id = $id;",
            _ => "DELETE FROM JobActions WHERE JobId = $id;"
        };
        command.Parameters.AddWithValue("$id", job.Id);
        await command.ExecuteNonQueryAsync(Ct);
        Assert.Empty(await Tick(await Due(card.Id)));
        Assert.Equal(0, await Due(card.Id));
        var status = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        Assert.Equal(expectedStatus, status.Status);
        Assert.Contains(reason, status.Reason);
        Assert.Empty(await _boards.GetLaneAutomationStatusesAsync(_root + "-foreign", card.Id, Ct));
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("assignment")]
    [InlineData("delete")]
    public async Task RemovedEntriesHaveDurableReasons_IncludingLegacyWriters(string operation)
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(other);
        await _boards.MoveCardAsync(_root, card.Id, lane, null, operation == "skip", Ct);
        if (operation == "assignment")
            await ExecuteSql($"UPDATE BoardLaneAutomations SET JobId = NULL, Revision = Revision + 1 WHERE ColumnId = '{lane}'; DELETE FROM BoardPendingAutomations WHERE CardId = '{card.Id}';");
        if (operation == "delete") await _boards.DeleteCardAsync(_root, card.Id, Ct);
        Assert.Equal(0, await Due(card.Id));
        if (operation == "delete")
        {
            // Deleted cards are not discoverable, but the cancellation evidence is retained.
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Reason FROM BoardLaneAutomationDispatch WHERE CardId = $id";
            command.Parameters.AddWithValue("$id", card.Id);
            Assert.Equal("Card was deleted.", await command.ExecuteScalarAsync(Ct));
        }
        else
        {
            var status = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
            Assert.Equal(operation == "skip" ? "Skipped" : "Cancelled", status.Status);
            Assert.Contains(operation == "skip" ? "caller's request" : "assignment changed", status.Reason);
        }
    }
}
