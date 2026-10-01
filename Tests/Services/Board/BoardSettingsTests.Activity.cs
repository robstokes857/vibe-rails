using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningAutomationIsVisibleInAnotherRoot_AndItsLaneDoesNotDependOnLoadedCards(bool manual)
    {
        var sessions = new Repository(_stateConnectionString);
        var (board, a, b, c) = await Lanes();
        var job = await Job();
        await _boards.SaveLaneAutomationAsync(_root, b, [job.Id], 0, Ct);
        var card = await Card(a);
        string run;
        if (manual) run = (await _jobs.EnqueueBoardCardRunAsync(_root, job.Id, card.Key, Ct))!;
        else
        {
            await _boards.MoveCardAsync(_root, card.Id, b, null, Ct);
            run = Assert.Single(await Tick(await Due(card.Id)));
        }
        var service = new BoardService(_boards, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe());
        var request = new BoardCardActivityRequest(board, [card.Id]);
        Assert.False(Assert.Single((await service.GetCardActivityAsync(_root, request, Ct)).Cards).HasActiveAutomation);
        Assert.True(await _jobs.StartRunAsync(run, Environment.ProcessId, Ct));
        // One run records both its shell and its Worker. Neither is a tab owned by this root.
        var shell = Guid.NewGuid().ToString();
        var worker = Guid.NewGuid().ToString();
        await sessions.CreateSessionAsync(shell, "shell", null, _root, Environment.ProcessId);
        await _jobs.LinkRunTerminalSessionAsync(run, shell, Ct);
        await sessions.CreateSessionAsync(worker, "codex", null, _root, Environment.ProcessId, jobRunId: run);
        await _jobs.LinkRunActionSessionAsync(run, Assert.Single((await _jobs.GetRunAsync(run, Ct))!.Actions!).Id, worker, Ct);
        await _boards.LinkSessionAsync(_root, card.Id, shell, "foreign-tab", "", "shell", "Workflow", BoardSessionRecord.AutomationOrigin, Ct);
        await _boards.LinkSessionAsync(_root, card.Id, worker, null, "", "codex", "Worker", BoardSessionRecord.McpOrigin, Ct);

        Assert.Single(await _boards.GetRunningAutomationsAsync(_root, Ct));
        var detail = (await service.GetCardAsync(_root, card.Id, Ct))!;
        Assert.True(detail.HasActiveAutomation);
        Assert.Null(detail.ActiveTabId);
        Assert.All(detail.Sessions, session => { Assert.True(session.IsAutomation); Assert.False(session.Active); });
        Assert.True(Assert.Single((await service.GetCardsAsync(_root, Ct, board)).Cards).HasActiveAutomation);
        Assert.True(Assert.Single((await service.GetCardsPageAsync(_root, new(), Ct, board)).Cards).HasActiveAutomation);
        Assert.True(Assert.Single((await service.GetCardActivityAsync(_root, request, Ct)).Cards).HasActiveAutomation);
        var emptyPageActivity = await service.GetCardActivityAsync(_root, request with { CardIds = [] }, Ct);
        Assert.Empty(emptyPageActivity.Cards);
        Assert.Equal(manual ? Array.Empty<string>() : [b], emptyPageActivity.ActiveAutomationColumnIds);
        Assert.Empty((await service.GetCardActivityAsync(_root + "-foreign", request, Ct)).ActiveAutomationColumnIds);
        var otherBoard = await _boards.CreateBoardAsync(_root, "Other", Ct);
        Assert.Empty((await service.GetCardActivityAsync(_root, request with { BoardId = otherBoard.Id }, Ct)).ActiveAutomationColumnIds);

        // Later card moves do not transfer a running lane's indicator to the new lane.
        await _boards.MoveCardAsync(_root, card.Id, c, null, Ct);
        Assert.Equal(manual ? Array.Empty<string>() : [b],
            (await service.GetCardActivityAsync(_root, request, Ct)).ActiveAutomationColumnIds);
        await _jobs.CompleteRunAsync(run, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.False((await service.GetCardAsync(_root, card.Id, Ct))!.HasActiveAutomation);
        Assert.Empty((await service.GetCardActivityAsync(_root, request, Ct)).ActiveAutomationColumnIds);
        Assert.False(Assert.Single((await service.GetCardActivityAsync(_root, request, Ct)).Cards).HasActiveAutomation);
    }

    [Fact]
    public async Task DeletedCardsDoNotExposeRunningAutomationActivity()
    {
        var (_, a, _, _) = await Lanes();
        var card = await Card(a);
        var job = await Job();
        var run = (await _jobs.EnqueueBoardCardRunAsync(_root, job.Id, card.Key, Ct))!;
        Assert.True(await _jobs.StartRunAsync(run, Environment.ProcessId, Ct));
        Assert.Single(await _boards.GetRunningAutomationsAsync(_root, Ct));
        await _boards.DeleteCardAsync(_root, card.Id, Ct);
        Assert.Empty(await _boards.GetRunningAutomationsAsync(_root, Ct));
    }
}
