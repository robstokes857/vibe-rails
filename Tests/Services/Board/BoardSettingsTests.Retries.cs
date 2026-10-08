using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DeterministicRetryReleasesNextStepOnlyAfterSuccess(bool manualCardRun, bool checkOnly, bool fixing)
    {
        var (_, lane, _, _) = await Lanes();
        var first = checkOnly
            ? await _jobs.CreateJobAsync(new("Check", _root, LLM.NotSet, null, "", null, true, [],
                Actions: [new(null, JobActionKind.Vca, Arguments: ["working-tree"])]), Ct)
            : await Job("Script");
        var next = await Job("Next");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, next.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var original = Assert.Single(await Tick(due));
        await _jobs.CompleteRunAsync(original, JobRunStatus.Failed, 1, "Failed", Ct);
        if (fixing)
        {
            var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
            var session = Guid.NewGuid().ToString();
            await service.LinkSessionAsync(_root, card.Id, session, null, "base:codex", "codex", "Worker", "work", Ct);
            var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0];
            await new BoardWorkflowService(_boards, service).ReportAsync(_root, card.Id, _root,
                BoardAuthor.Agent("Codex", "codex", session), "fixing", "Fixing the failed check", entry.EventKey, null, Ct);
            Assert.Equal("Fixing", (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0].StepStatus);
        }
        var retry = (manualCardRun
            ? await _jobs.EnqueueBoardCardRunAsync(_root, first.Id, card.Key, Ct)
            : await _jobs.EnqueueRetryAsync(original, Ct))!;
        var queued = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0];
        Assert.Equal(retry, queued.RunId);
        Assert.Equal("Queued", queued.StepStatus);
        Assert.Empty(await Tick(due + 1));
        await _jobs.CompleteRunAsync(retry, JobRunStatus.Failed, 1, "Retry failed", Ct);
        var secondRetry = (await _jobs.EnqueueRetryAsync(retry, Ct))!;
        await _jobs.CompleteRunAsync(secondRetry, JobRunStatus.Succeeded, 0, null, Ct);
        var successor = Assert.Single(await Tick(due + 2));
        Assert.Equal(next.Id, (await _jobs.GetRunAsync(successor, Ct))!.JobId);
        var passed = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0];
        Assert.Equal("Passed", passed.StepStatus);
        Assert.Equal(secondRetry, passed.RunId);
        Assert.Contains(await _jobs.GetBoardCardRunsAsync(_root, card.Key, Ct), r => r.Id == secondRetry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryFromPreviousEntryCannotSatisfyReentry(bool manualCardRun)
    {
        var (_, lane, away, _) = await Lanes();
        var first = await Job("First");
        var next = await Job("Next");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, next.Id], 0, Ct);
        var card = await Card(lane);
        var original = Assert.Single(await Tick(await Due(card.Id)));
        await _jobs.CompleteRunAsync(original, JobRunStatus.Failed, 1, "Failed", Ct);
        var retry = (manualCardRun
            ? await _jobs.EnqueueBoardCardRunAsync(_root, first.Id, card.Key, Ct)
            : await _jobs.EnqueueRetryAsync(original, Ct))!;
        await _boards.MoveCardAsync(_root, card.Id, away, null, Ct);
        await _boards.MoveCardAsync(_root, card.Id, lane, null, Ct);
        await _jobs.CompleteRunAsync(retry, JobRunStatus.Succeeded, 0, null, Ct);
        var newDue = await Due(card.Id);
        var fresh = Assert.Single(await Tick(newDue));
        Assert.Equal(first.Id, (await _jobs.GetRunAsync(fresh, Ct))!.JobId);
        var steps = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Where(e => e.IsCurrent).ToList();
        Assert.Equal("Queued", steps[0].StepStatus);
        Assert.Equal(fresh, steps[0].RunId);
        Assert.Empty(await Tick(newDue + 1));
    }

    [Fact]
    public async Task SkippingAnActiveDeterministicRetryWaitsForThatRetryToStop()
    {
        var (_, lane, _, _) = await Lanes();
        var first = await Job("First");
        var next = await Job("Next");
        await _boards.SaveLaneAutomationAsync(_root, lane, [first.Id, next.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var original = Assert.Single(await Tick(due));
        await _jobs.CompleteRunAsync(original, JobRunStatus.Failed, 1, "Failed", Ct);
        var retry = (await _jobs.EnqueueRetryAsync(original, Ct))!;
        await _jobs.StartRunAsync(retry, 1234, Ct);
        var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0];
        var service = new BoardCardAutomationService(_boards, _jobs, Mock.Of<IJobService>());
        await service.SkipAsync(_root, card.Id, first.Id, entry.EventKey, Ct);
        Assert.True(await _jobs.IsCancelRequestedAsync(retry, Ct));
        Assert.Equal("Stopping", (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct))[0].StepStatus);
        Assert.Empty(await Tick(due + 1));
        await _jobs.CompleteRunAsync(retry, JobRunStatus.Cancelled, 1, "Stopped", Ct);
        Assert.Single(await Tick(due + 2));
    }
}
