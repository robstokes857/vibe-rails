using VibeRails.Services.Board;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>VIBE-338 item 4: an agent skips one lane step with a stated reason.</summary>
public sealed partial class BoardToolTests
{
    private const string SkipReason = "This card only edits documentation, so a Code quality check has nothing to grade.";

    private async Task<(BoardCardRecord Card, BoardLaunchEvent Entry, string Preset)> CardWithLaneStepAsync()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Reviewer", "", "codex", InitialPrompt: "Review"), Ct)).Preset;
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.SaveLaunchLaneAsync(_project, lanes[0].Id, new([new(preset.Id, "preset", "result")], 0), Ct);
        var card = await _store.CreateCardAsync(_project, new(lanes[0].Id, "Docs", "", null, "medium", null, [], false), Ct);
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Worker", "work", Ct);
        return (card, Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct)), preset.Id);
    }

    [Fact]
    public async Task SkipLaneStepSavesTheReasonAndStaysUntilAPersonDismissesIt()
    {
        var (card, entry, _) = await CardWithLaneStepAsync();

        Assert.Contains("Give a reason of 20", await _tool.SkipLaneStep(entry.Id, "not needed", card.Key, Ct));
        Assert.Contains("No current lane step", await _tool.SkipLaneStep("nope", SkipReason, card.Key, Ct));
        Assert.Null(Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct)).Verdict);

        var reply = await _tool.SkipLaneStep(entry.Id, SkipReason, card.Key, Ct);
        Assert.StartsWith("Skipped \"Reviewer\"", reply);
        var skipped = Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct));
        Assert.Equal(("skipped", true), (skipped.Verdict, skipped.SkipRequested));
        Assert.Equal(BoardLaunchEvent.AgentSkipPrefix + SkipReason, skipped.Summary);
        Assert.NotNull(skipped.SkippedByAgent);
        // Owner decision 2026-11-16: the lane carries on at once.
        Assert.True(BoardLaunchWorkflow.ReleasesNext(skipped));

        var comment = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
        Assert.Contains("Skipped lane step **Reviewer**", comment.Body);
        Assert.Contains(SkipReason, comment.Body);
        Assert.Equal(_resolver.CurrentSessionId, comment.Author.SessionId);

        // The tile, the editor and agents all see it until a person dismisses it.
        var shown = (await _service.GetCardAsync(_project, card.Id, Ct))!.AgentSkip;
        Assert.Equal(("Reviewer", SkipReason, 1), (shown!.StepName, shown.Reason, shown.OpenCount));
        Assert.Equal(SkipReason, (await _service.GetCardsAsync(_project, Ct)).Cards.Single(c => c.Id == card.Id).AgentSkip!.Reason);
        Assert.Contains("Skipped by agent: " + SkipReason, await _tool.GetBoardCard(card.Key, cancellationToken: Ct));
        Assert.Contains("Skipped by agent: " + SkipReason, await _tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct));
        Assert.Contains("already skipped", await _tool.SkipLaneStep(entry.Id, SkipReason, card.Key, Ct));

        Assert.Equal(1, await _store.DismissAgentSkipsAsync(_project, card.Id, Ct));
        Assert.Null((await _service.GetCardAsync(_project, card.Id, Ct))!.AgentSkip);
        // Dismissing reads the reason; the step itself stays skipped and the comment stays.
        Assert.Equal("skipped", Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct)).Verdict);
    }

    [Fact]
    public async Task SkipLaneStepNeverStopsARunningStep()
    {
        var (card, entry, preset) = await CardWithLaneStepAsync();
        var run = await _launchService.EnqueueAsync("preset", preset, new(_project, _project, "board-lane", "skip-test",
            CardId: card.Id, LaneEntryId: entry.Id), Ct);
        await _store.AcknowledgeLaunchEventAsync(entry.Id, run.Id, Ct);
        var claim = (await _launches.TryClaimAsync(run.Id, 3, 1, DateTime.UtcNow, Ct))!;
        await _launches.StartAsync(claim, Guid.NewGuid().ToString(), "Review", _project, 1, DateTime.UtcNow, ct: Ct);

        Assert.Contains("This step is running", await _tool.SkipLaneStep(entry.Id, SkipReason, card.Key, Ct));
        var unchanged = Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct));
        Assert.False(unchanged.SkipRequested);
        Assert.Null((await _service.GetCardAsync(_project, card.Id, Ct))!.AgentSkip);
    }

    [Fact]
    public async Task MoveSkippingAutomationsNeedsAReasonAndRecordsIt()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Reviewer", "", "codex", InitialPrompt: "Review"), Ct)).Preset;
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.SaveLaunchLaneAsync(_project, lanes[1].Id, new([new(preset.Id, "preset", "result")], 0), Ct);
        var card = await _store.CreateCardAsync(_project, new(lanes[0].Id, "Spike", "", null, "medium", null, [], false), Ct);

        Assert.Contains("Give a reason of 20", await _tool.MoveBoardCard(card.Key, lanes[1].Name, skipAutomations: true, cancellationToken: Ct));
        Assert.Equal(lanes[0].Id, (await _store.FindCardAsync(_project, card.Id, Ct))!.ColumnId);
        // A preview never needs one.
        Assert.StartsWith("Preview:", await _tool.MoveBoardCard(card.Key, lanes[1].Name, skipAutomations: true, preview: true, cancellationToken: Ct));

        Assert.StartsWith("Moved", await _tool.MoveBoardCard(card.Key, lanes[1].Name, skipAutomations: true, cancellationToken: Ct, skipReason: SkipReason));
        var comment = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
        Assert.EndsWith("Reason: " + SkipReason, comment.Body);
    }
}
