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
        var runs = new VibeRails.Data.Sqlite.LaunchStore($"Data Source={Path.Combine(_root, "launches.db")};Pooling=False");
        var launches = new VibeRails.Services.Launches.LaunchService(runs, Mock.Of<VibeRails.Services.Jobs.IAutomationScriptService>());
        var preset = (await launches.SavePresetAsync(null, new("Worker", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var (board, a, b, c) = await Lanes();
        await _boards.SaveLaunchLaneAsync(_root, b, new([new(preset.Id, "preset", "result")], 0), Ct);
        var card = await Card(manual ? a : b);
        var entry = (await _boards.GetLaunchEventsAsync(_root, card.Id, Ct)).FirstOrDefault();
        var run = await launches.EnqueueAsync("preset", preset.Id,
            new(_root, _root, manual ? "manual" : "board-lane", "test", BoardId: board, CardId: card.Id, LaneEntryId: entry?.Id, SessionOrigin: "automation"), Ct);
        if (entry is not null) await _boards.AcknowledgeLaunchEventAsync(entry.Id, run.Id, Ct);
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), launchRuns: runs);
        var request = new BoardCardActivityRequest(board, [card.Id]);
        Assert.False(Assert.Single((await service.GetCardActivityAsync(_root, request, Ct)).Cards).HasActiveAutomation);
        var claim = (await runs.TryClaimAsync(run.Id, 3, Environment.ProcessId, DateTime.UtcNow, Ct))!;
        var session = Guid.NewGuid().ToString();
        await runs.StartAsync(claim, session, "Work", _root, Environment.ProcessId, DateTime.UtcNow, ct: Ct);
        await _boards.LinkSessionAsync(_root, card.Id, session, "foreign-tab", "", "codex", "Worker", BoardSessionRecord.AutomationOrigin, Ct);
        var detail = (await service.GetCardAsync(_root, card.Id, Ct))!;
        Assert.True(detail.HasActiveAutomation);
        Assert.Null(detail.ActiveTabId);
        Assert.True(Assert.Single(detail.Sessions).IsAutomation);
        Assert.True(Assert.Single((await service.GetCardsPageAsync(_root, new(), Ct, board)).Cards).HasActiveAutomation);
        var empty = await service.GetCardActivityAsync(_root, request with { CardIds = [] }, Ct);
        Assert.Empty(empty.Cards);
        Assert.Equal(manual ? Array.Empty<string>() : [b], empty.ActiveAutomationColumnIds);
        Assert.Empty((await service.GetCardActivityAsync(_root + "-foreign", request, Ct)).ActiveAutomationColumnIds);
        await _boards.MoveCardAsync(_root, card.Id, c, null, Ct);
        Assert.Equal(manual ? Array.Empty<string>() : [b], (await service.GetCardActivityAsync(_root, request, Ct)).ActiveAutomationColumnIds);
        await runs.CompleteAsync(claim, "succeeded", 0, ct: Ct);
        Assert.False((await service.GetCardAsync(_root, card.Id, Ct))!.HasActiveAutomation);
        Assert.Empty((await service.GetCardActivityAsync(_root, request, Ct)).ActiveAutomationColumnIds);
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
