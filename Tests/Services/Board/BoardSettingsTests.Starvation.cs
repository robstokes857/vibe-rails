using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPredecessorOnOlderCard_DoesNotStarveNewerCardsStep(bool restart)
    {
        var (_, lane, _, _) = await Lanes();
        var prerequisite = await Job("Prerequisite");
        var next = await Job("Next step");
        await _boards.SaveLaneAutomationAsync(_root, lane, [prerequisite.Id, next.Id], 0, Ct);
        var older = await Card(lane);
        var due = await Due(older.Id);
        var olderRun = Assert.Single(await Tick(due));
        await _jobs.CompleteRunAsync(olderRun, JobRunStatus.Failed, 1, "Prerequisite failed", Ct);

        var newer = await Card(lane);
        await SetDue(newer, due + 1);
        var newerRun = Assert.Single(await Tick(due + 1));
        Assert.Contains(newer.Key, (await _jobs.GetRunAsync(newerRun, Ct))!.TriggerKey);
        await _jobs.CompleteRunAsync(newerRun, JobRunStatus.Succeeded, 0, null, Ct);

        var nextRun = Assert.Single(await Tick(due + 2, restart ? ReopenJobs() : null));

        var queued = (await _jobs.GetRunAsync(nextRun, Ct))!;
        Assert.Equal(next.Id, queued.JobId);
        Assert.Contains(newer.Key, queued.TriggerKey);
        var olderStep = (await _boards.GetLaneAutomationStatusesAsync(_root, older.Id, Ct))
            .Single(s => s.JobId == next.Id);
        Assert.Equal(BoardStepStatus.Waiting, olderStep.Status);
        Assert.Contains("Prerequisite: Failed", olderStep.Reason);
        Assert.Null(olderStep.RunId);
        Assert.Single(await _boards.GetPendingLaneAutomationsAsync(_root, older.Id, Ct));
    }

    [Fact]
    public async Task DueEntryBeforeFirstAttempt_DoesNotGuessBackendAvailability()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        await SetDue(card, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

        var status = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));

        Assert.Equal(BoardStepStatus.Waiting, status.Status);
        Assert.Equal("Waiting for the scheduler to evaluate this lane entry.", status.Reason);
    }
}
