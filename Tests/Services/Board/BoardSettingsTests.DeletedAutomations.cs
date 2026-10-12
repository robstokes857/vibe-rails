using VibeRails.DB;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Fact]
    public async Task DeletingAnAutomation_UnlinksItFromEveryLane_AndCancelsThoseLanesPendingEntries()
    {
        var (_, a, b, c) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        var third = await Job("Third");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id, second.Id, third.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, b, [second.Id], 0, Ct);
        await _boards.SaveLaneAutomationAsync(_root, c, [third.Id], 0, Ct);
        var settling = await Card(a);
        var untouched = await Card(c);
        Assert.Equal(3, (await _boards.GetPendingLaneAutomationsAsync(_root, settling.Id, Ct)).Count);

        Assert.True(await _jobs.SoftDeleteJobAsync(second.Id, Ct));

        var laneA = (await _boards.GetLaneAutomationAsync(_root, a, Ct))!;
        Assert.Equal(new[] { first.Id, third.Id }, laneA.JobIds);
        Assert.Equal(2, laneA.Revision);
        var laneB = (await _boards.GetLaneAutomationAsync(_root, b, Ct))!;
        Assert.Empty(laneB.JobIds);
        Assert.Equal(2, laneB.Revision);
        var laneC = (await _boards.GetLaneAutomationAsync(_root, c, Ct))!;
        Assert.Equal(new[] { third.Id }, laneC.JobIds);
        Assert.Equal(1, laneC.Revision);
        // Like a lane save, unlinking cancels the changed lane's pending entries; other lanes keep theirs.
        Assert.Empty(await _boards.GetPendingLaneAutomationsAsync(_root, settling.Id, Ct));
        Assert.Single(await _boards.GetPendingLaneAutomationsAsync(_root, untouched.Id, Ct));
        // Later entries queue only the remaining selection.
        var later = await Card(a);
        Assert.Equal(new[] { first.Id, third.Id }, (await _boards.GetPendingLaneAutomationsAsync(_root, later.Id, Ct)).Select(p => p.JobId).Order());
        // An open editor must reload, and the deleted job cannot be selected again.
        await Assert.ThrowsAsync<BoardConflictException>(() => _boards.SaveLaneAutomationAsync(_root, a, [first.Id], 1, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _boards.SaveLaneAutomationAsync(_root, a, [first.Id, second.Id], 2, Ct));
        Assert.Equal(0, await _boards.RemoveJobFromLanesAsync(second.Id, Ct));
        Assert.False(await _jobs.SoftDeleteJobAsync(second.Id, Ct));
    }

    [Fact]
    public async Task LaneSettingsRead_PrunesSelectionsDeletedWithoutThisBoard_AndKeepsOtherUnavailableEntries()
    {
        var (_, a, _, _) = await Lanes();
        var first = await Job("First");
        var second = await Job("Second");
        var foreign = await Job("Foreign", _root + "-other");
        await _boards.SaveLaneAutomationAsync(_root, a, [first.Id, second.Id], 0, Ct);
        // A legacy cross-project link and a definition this state.db never held stay listed as unavailable.
        await ExecuteSql($"INSERT INTO BoardLaneAdditionalAutomations (ColumnId, JobId, Position) VALUES ('{a}', {foreign.Id}, 2), ('{a}', 99999, 3);");
        // An older binary, or a host opened without the Board store, deletes the first job without unlinking it.
        Assert.True(await new JobStore(_stateConnectionString).SoftDeleteJobAsync(first.Id, Ct));
        Assert.Equal(new[] { first.Id, second.Id, foreign.Id, 99999 }, (await _boards.GetLaneAutomationAsync(_root, a, Ct))!.JobIds);

        var settings = (await new BoardAutomationService(_boards, _jobs).GetAsync(_root, a, Ct))!;
        Assert.Equal(new[] { second.Id, foreign.Id, 99999 }, settings.JobIds);
        Assert.Equal(2, settings.Revision);
        Assert.DoesNotContain(settings.Jobs, option => option.Id == first.Id);
        Assert.Equal(settings.JobIds, (await _boards.GetLaneAutomationAsync(_root, a, Ct))!.JobIds);
        // Idempotent: a second read changes nothing.
        Assert.Equal(2, (await new BoardAutomationService(_boards, _jobs).GetAsync(_root, a, Ct))!.Revision);
    }
}
