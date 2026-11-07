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
    public async Task FailedSkipReceiptRollsBackTheSkip_AndCanBeRetried()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var entry = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER RejectSkipReceipt BEFORE INSERT ON BoardComments
            BEGIN SELECT RAISE(ABORT, 'receipt write failed'); END;
            """;
        await trigger.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<SqliteException>(() => service.SkipAsync(_root, card.Id, job.Id, entry.EventKey, Ct));
        Assert.Single(await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct));
        Assert.Equal("Waiting", Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Status);
        Assert.Empty((await _boards.GetCardDetailAsync(_root, card.Id, Ct))!.Comments);
        trigger.CommandText = "DROP TRIGGER RejectSkipReceipt;";
        await trigger.ExecuteNonQueryAsync(Ct);
        var result = (await service.SkipAsync(_root, card.Id, job.Id, entry.EventKey, Ct))!;
        Assert.Equal("Skipped", Assert.Single(result.LaneEntries!).Status);
        Assert.Empty(await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct));
        Assert.Single((await _boards.GetCardDetailAsync(_root, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task WaitingCardsAreVisibleAcrossListDetailAndActivity_AndAdvanceAfterBusyRun()
    {
        var (board, _, lane, _) = await Lanes();
        var runs = new VibeRails.Data.Sqlite.LaunchStore($"Data Source={Path.Combine(_root, "launches.db")};Pooling=False");
        var launches = new VibeRails.Services.Launches.LaunchService(runs, Mock.Of<IAutomationScriptService>());
        var preset = (await launches.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        await _boards.SaveLaunchLaneAsync(_root, lane, new([new(preset.Id, "preset", "result")], 0), Ct);
        var first = await Card(lane);
        var entry = Assert.Single(await _boards.GetLaunchEventsAsync(_root, first.Id, Ct));
        var run = await launches.EnqueueAsync("preset", preset.Id, new(_root, _root, "board-lane", "first", BoardId: board, CardId: first.Id, LaneEntryId: entry.Id), Ct);
        await _boards.AcknowledgeLaunchEventAsync(entry.Id, run.Id, Ct);
        var claim = (await runs.TryClaimAsync(run.Id, 3, 1, DateTime.UtcNow, Ct))!;
        var second = await Card(lane);
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), launchRuns: runs);
        Assert.True((await service.GetCardAsync(_root, second.Id, Ct))!.HasWaitingAutomation);
        Assert.True((await service.GetCardsAsync(_root, Ct, board)).Cards.Single(c => c.Id == second.Id).HasWaitingAutomation);
        var request = new BoardCardActivityRequest(board, [first.Id, second.Id]);
        var activity = await service.GetCardActivityAsync(_root, request, Ct);
        Assert.True(activity.Cards.Single(c => c.Id == second.Id).HasWaitingAutomation);
        Assert.False(activity.Cards.Single(c => c.Id == first.Id).HasWaitingAutomation);
        await runs.CompleteAsync(claim, "succeeded", 0, ct: Ct);
        var secondEntry = Assert.Single(await _boards.GetLaunchEventsAsync(_root, second.Id, Ct));
        var next = await launches.EnqueueAsync("preset", preset.Id, new(_root, _root, "board-lane", "second", BoardId: board, CardId: second.Id, LaneEntryId: secondEntry.Id), Ct);
        await _boards.AcknowledgeLaunchEventAsync(secondEntry.Id, next.Id, Ct);
        Assert.NotNull(await runs.TryClaimAsync(next.Id, 3, 1, DateTime.UtcNow, Ct));
        Assert.False((await service.GetCardAsync(_root, second.Id, Ct))!.HasWaitingAutomation);
    }

    [Fact]
    public async Task SkipTargetsOneExactEntry_PreservesOtherCardsAndReentries_AndStopsCommittedRuns()
    {
        var (_, from, lane, _) = await Lanes();
        var firstJob = await Job();
        var secondJob = await Job("Other script");
        await _boards.SaveLaneAutomationAsync(_root, lane, [firstJob.Id, secondJob.Id], 0, Ct);
        var card = await Card(lane);
        var other = await Card(lane);
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Single(e => e.JobId == secondJob.Id);
        Assert.Null(await service.SkipAsync(_root + "-other", card.Id, secondJob.Id, entry.EventKey, Ct));
        var skipped = (await service.SkipAsync(_root, card.Id, secondJob.Id, entry.EventKey, Ct))!;
        Assert.Equal("Skipped", skipped.LaneEntries!.Single(e => e.JobId == secondJob.Id).Status);
        Assert.Single(await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct));
        Assert.Equal(2, (await _boards.GetPendingLaneAutomationsAsync(_root, other.Id, Ct)).Count);
        Assert.Contains((await _boards.GetCardDetailAsync(_root, card.Id, Ct))!.Comments,
            comment => comment.Body.Contains("Skipped lane Automation"));
        await _boards.MoveCardAsync(_root, card.Id, from, null, Ct);
        await _boards.MoveCardAsync(_root, card.Id, lane, null, Ct);
        await Assert.ThrowsAsync<BoardConflictException>(() => service.SkipAsync(_root, card.Id, secondJob.Id, entry.EventKey, Ct));
        Assert.Equal(2, (await _boards.GetPendingLaneAutomationsAsync(_root, card.Id, Ct)).Count);
        // Remove the older card so this card can start its first step.
        await _boards.MoveCardAsync(_root, other.Id, from, null, Ct);
        Assert.Single(await Tick(await Due(card.Id)));
        var queued = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).First(e => e.RunId is not null);
        var result = await service.SkipAsync(_root, card.Id, queued.JobId, queued.EventKey, Ct);
        Assert.Equal("Skipped", result!.LaneEntries!.Single(e => e.EventKey == queued.EventKey).StepStatus);
        Assert.Equal(JobRunStatus.Cancelled, (await _jobs.GetRunAsync(queued.RunId!, Ct))!.Status);
    }
}
