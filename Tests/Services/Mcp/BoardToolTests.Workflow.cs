using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task CardAndAgentStatusUseTheSavedCheckOutcome()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct))[0];
        var action = await _launchService.SaveActionAsync(_project, null, new("Quality", "", "code-quality", Check: new("working-tree")), Ct);
        await _store.SaveLaunchLaneAsync(_project, lane.Id, new([new(action.Id, "action", "result")], 0), Ct);
        var card = await _store.CreateCardAsync(_project, new(lane.Id, "Check", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct));
        var run = await _launchService.EnqueueAsync("action", action.Id, new(_project, _project, "board-lane", "check", BoardId: card.BoardId, CardId: card.Id, LaneEntryId: entry.Id), Ct);
        await _store.AcknowledgeLaunchEventAsync(entry.Id, run.Id, Ct);
        var claim = (await _launches.TryClaimAsync(run.Id, 3, 1, DateTime.UtcNow, Ct))!;
        Assert.True(await _store.SaveCheckAsync(_project, new("check", card.Id, run.Id, action.Id,
            "Code quality", "Completed with limitations", "working-tree", null, null, null, null, "1",
            DateTime.UtcNow, DateTime.UtcNow, "5 files analyzed; 7 skipped.", 12, 5, 7, 0, []), Ct));
        await _launches.CompleteAsync(claim, "succeeded", 0, ct: Ct);

        foreach (var response in new[] { await _tool.GetBoardCard(card.Key, cancellationToken: Ct),
            await _tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct) })
        {
            Assert.Contains("Completed with limitations", response);
            Assert.Contains("5 files analyzed; 7 skipped.", response);
            Assert.DoesNotContain("Quality · Passed", response);
        }
    }

    [Fact]
    public async Task WorkflowToolRequiresManagedSessionAndNeverAllowsAgentSkip()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, new(null, "Workflow", "", null, "medium", null, [], false), Ct);
        Assert.Contains("current VibeRails agent session", await _tool.ReportAutomationStep("failed", "Blocked", card: card.Key, cancellationToken: Ct));
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Worker", "work", Ct);
        Assert.Contains("Only the user can skip", await _tool.ReportAutomationStep("skipped", "Bypass", card: card.Key, cancellationToken: Ct));
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task WorkflowToolRecordsOnlyTheCurrentRunsDecision()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Worker", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.SaveLaunchLaneAsync(_project, lanes[0].Id, new([new(preset.Id, "preset", "result")], 0), Ct);
        var card = await _store.CreateCardAsync(_project, new(lanes[0].Id, "Workflow", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct));
        var run = await _launchService.EnqueueAsync("preset", preset.Id, new(_project, _project, "board-lane", "test", CardId: card.Id, LaneEntryId: entry.Id), Ct);
        await _store.AcknowledgeLaunchEventAsync(entry.Id, run.Id, Ct);
        var claim = (await _launches.TryClaimAsync(run.Id, 3, 1, DateTime.UtcNow, Ct))!;
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await _launches.StartAsync(claim, _resolver.CurrentSessionId, "Work", _project, 1, DateTime.UtcNow, ct: Ct);
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Worker", "automation", Ct);
        Assert.StartsWith("Workflow status recorded", await _tool.ReportAutomationStep("failed", "Needs correction", card: card.Key, cancellationToken: Ct));
        Assert.Equal("failed", Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct)).Verdict);
        await _store.MoveCardAsync(_project, card.Id, lanes[1].Id, null, Ct);
        await _store.MoveCardAsync(_project, card.Id, lanes[0].Id, null, Ct);
        Assert.StartsWith("FAIL:", await _tool.ReportAutomationStep("passed", "Old result", eventKey: entry.Id, card: card.Key, cancellationToken: Ct));
    }
}
