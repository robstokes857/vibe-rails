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
    private async Task<(string Status, string Reason, long Attempt)> DispatchReceipt(BoardLaneAutomationEvent entry)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT Status, Reason, LastAttemptUnixMs FROM BoardLaneAutomationDispatch WHERE EventKey = $event AND JobId = $job";
        query.Parameters.AddWithValue("$event", entry.EventKey);
        query.Parameters.AddWithValue("$job", entry.JobId);
        await using var reader = await query.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    [Fact]
    public async Task BlockedTicksReuseReceiptWithoutWriterLockAndStillObserveCompletion()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var run = Assert.Single(await Tick(due));
        Assert.Empty(await Tick(due + 10000));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(due + 20000).UtcDateTime;
        var entry = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        var before = await DispatchReceipt(entry);
        Assert.Empty(await Tick(due + 20000));
        Assert.Equal(before, await DispatchReceipt(entry));

        // A cached receipt must not even try BEGIN IMMEDIATE on board.db.
        await using (var writer = new SqliteConnection(_connectionString))
        {
            await writer.OpenAsync(Ct);
            await using var transaction = writer.BeginTransaction(deferred: false);
            await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", before.Reason), now.AddSeconds(10), Ct);
        }
        await _jobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        var next = Assert.Single(await Tick(due + 40000));
        Assert.Equal(second.Id, (await _jobs.GetRunAsync(next, Ct))!.JobId);
        Assert.Equal("Queued", (await DispatchReceipt(entry)).Status);
    }

    [Fact]
    public async Task WaitingReceiptCacheRefreshesChangedReasonsAndExpires()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        await Card(lane);
        var now = DateTime.UtcNow.AddMinutes(2);
        var entry = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "First reason"), now, Ct);
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Changed reason"), now.AddSeconds(10), Ct);
        var changed = await DispatchReceipt(entry);
        Assert.Equal("Changed reason", changed.Reason);
        Assert.Equal(new DateTimeOffset(now.AddSeconds(10)).ToUnixTimeMilliseconds(), changed.Attempt);
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", changed.Reason), now.AddMinutes(1), Ct);
        Assert.Equal(changed, await DispatchReceipt(entry));
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", changed.Reason), now.AddMinutes(6), Ct);
        Assert.Equal(new DateTimeOffset(now.AddMinutes(6)).ToUnixTimeMilliseconds(), (await DispatchReceipt(entry)).Attempt);
    }

    [Fact]
    public async Task FailedWaitingReceiptIsRetriedAndReentryHasItsOwnCacheKey()
    {
        var (_, lane, other, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var now = DateTime.UtcNow.AddMinutes(2);
        var entry = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        await ExecuteSql("CREATE TRIGGER FailWaiting BEFORE INSERT ON BoardLaneAutomationDispatch BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Busy"), now, Ct));
        await ExecuteSql("DROP TRIGGER FailWaiting;");
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Busy"), now, Ct);
        Assert.Equal("Waiting", (await DispatchReceipt(entry)).Status);
        var anotherRoot = new BoardStore(_connectionString, _stateConnectionString);
        await anotherRoot.MoveCardAsync(_root, card.Id, other, null, Ct);
        await anotherRoot.MoveCardAsync(_root, card.Id, lane, null, Ct);
        var next = Assert.Single(await _boards.GetDueLaneAutomationsAsync(now, Ct));
        Assert.NotEqual(entry.EventKey, next.EventKey);
        await _boards.RecordLaneAutomationDispatchAsync(next, new("Waiting", "Busy"), now.AddSeconds(10), Ct);
        Assert.Equal("Waiting", (await DispatchReceipt(next)).Status);
        await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Busy"), now.AddSeconds(20), Ct);
        Assert.Equal("Cancelled", (await DispatchReceipt(entry)).Status);
    }

    [Fact]
    public async Task SuppressedWaitingWritesStillRotateTheHundredJobDrain()
    {
        var (board, _, _, _) = await Lanes();
        for (var i = 0; i < 101; i++)
        {
            var lane = await _boards.CreateColumnAsync(_root, $"Lane {i}", "#123456", Ct, board);
            var job = await Job($"Job {i}");
            await _boards.SaveLaneAutomationAsync(_root, lane.Id, [job.Id], 0, Ct);
            await Card(lane.Id);
        }
        var now = DateTime.UtcNow.AddMinutes(2);
        var first = await _boards.GetDueLaneAutomationsAsync(now, Ct);
        Assert.Equal(100, first.Count);
        foreach (var entry in first)
            await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Busy"), now, Ct);
        var second = await _boards.GetDueLaneAutomationsAsync(now.AddSeconds(10), Ct);
        Assert.Contains(second, e => first.All(f => f.EventKey != e.EventKey));
        var omitted = Assert.Single(first, e => second.All(s => s.EventKey != e.EventKey));
        foreach (var entry in second)
            await _boards.RecordLaneAutomationDispatchAsync(entry, new("Waiting", "Busy"), now.AddSeconds(10), Ct);
        var third = await _boards.GetDueLaneAutomationsAsync(now.AddSeconds(20), Ct);
        Assert.Equal(omitted.EventKey, third[0].EventKey);
        var cached = second.First(e => first.Any(f => f.EventKey == e.EventKey));
        Assert.Equal(new DateTimeOffset(now).ToUnixTimeMilliseconds(), (await DispatchReceipt(cached)).Attempt);
    }

    [Fact]
    public async Task LaneBatchHandlesHundredCardsAndRefreshesOtherRootsRunsAndSkips()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, second.Id], 0, Ct);
        var cards = new List<BoardCardRecord>();
        for (var i = 0; i < 101; i++) cards.Add(await Card(lane));
        var due = await Due(cards[^1].Id) + 1;
        // New cards go at the top. Exercise a retry that remains inside the first 100.
        // Base the override on the oldest entry, even when creating 101 cards takes over a second.
        await SetDue(cards[^1], await Due(cards[0].Id) - 1000);
        var original = Assert.Single(await Tick(due));
        await _jobs.CompleteRunAsync(original, JobRunStatus.Failed, 1, "Failed", Ct);
        var retry = (await _jobs.EnqueueRetryAsync(original, Ct))!;
        await _jobs.CompleteRunAsync(retry, JobRunStatus.Succeeded, 0, null, Ct);
        var batch = await _boards.GetLaneWorkflowsAsync(_root, lane, Ct);
        Assert.Equal(100, batch.Count);
        Assert.DoesNotContain(batch, w => w.CardId == cards[0].Id);
        var passed = Assert.Single(batch.SelectMany(w => w.Steps), s => s.StepStatus == "Passed");
        Assert.Equal(retry, passed.RunId);
        foreach (var workflow in batch.Take(3))
            Assert.Equal((await _boards.GetLaneAutomationStatusesAsync(_root, workflow.CardId, Ct)).Where(s => s.IsCurrent), workflow.Steps);
        Assert.Empty(await _boards.GetLaneWorkflowsAsync(_root + "-foreign", lane, Ct));

        var anotherRoot = new BoardStore(_connectionString, _stateConnectionString);
        var service = new BoardCardAutomationService(anotherRoot, _jobs, Mock.Of<IJobService>());
        var waiting = batch.First(w => w.Steps.All(s => s.Status == "Waiting"));
        await service.SkipAsync(_root, waiting.CardId, first.Id, waiting.Steps[0].EventKey, Ct);
        var refreshed = await _boards.GetLaneWorkflowsAsync(_root, lane, Ct);
        Assert.Equal("Skipped", refreshed.Single(w => w.CardId == waiting.CardId).Steps[0].StepStatus);
        var nextRuns = await Tick(due + 10000);
        Assert.NotEmpty(nextRuns);
        var otherJobs = new JobStore(_stateConnectionString, anotherRoot);
        foreach (var run in nextRuns) await otherJobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.True((await _boards.GetLaneWorkflowsAsync(_root, lane, Ct)).SelectMany(w => w.Steps).Count(s => s.StepStatus == "Passed") > 1);
    }
}
