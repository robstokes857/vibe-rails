using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// What the dashboard shows on a lane and a tile is also what an agent reads: named lane entries,
/// the default lane for new cards, the toolbar's filters, the pin and the Agent workflow.
/// </summary>
public sealed partial class BoardToolTests
{
    [Fact]
    public async Task ListBoardCards_FiltersLikeTheToolbar_AndAcceptsTheProviderShorthand()
    {
        await _tool.CreateBoardCard("Claude's bug", priority: "critical", type: "bug", assignee: "claude", cancellationToken: Ct);
        await _tool.CreateBoardCard("Codex's task", priority: "low", assignee: "base:codex", cancellationToken: Ct);
        await _tool.UpdateBoardCard("PROJ-2", blocked: true, cancellationToken: Ct);
        await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: "Needs a decision on the schema change.", cancellationToken: Ct);

        // create and update take "claude" for base:claude; the filter used to compare the raw text and match nothing.
        var shorthand = await _tool.ListBoardCards(assignee: "claude", cancellationToken: Ct);
        Assert.Contains("Claude's bug", shorthand);
        Assert.DoesNotContain("Codex's task", shorthand);
        Assert.Equal(shorthand, await _tool.ListBoardCards(assignee: "base:claude", cancellationToken: Ct));

        Assert.Equal(shorthand, await _tool.ListBoardCards(priority: "CRITICAL", cancellationToken: Ct));
        Assert.Equal(shorthand, await _tool.ListBoardCards(flagged: true, cancellationToken: Ct));
        var blocked = await _tool.ListBoardCards(blocked: true, cancellationToken: Ct);
        Assert.Contains("Codex's task", blocked);
        Assert.DoesNotContain("Claude's bug", blocked);
        Assert.Equal(blocked, await _tool.ListBoardCards(flagged: false, cancellationToken: Ct));
        Assert.Equal("No cards match.", await _tool.ListBoardCards(priority: "medium", cancellationToken: Ct));
    }

    [Fact]
    public async Task ListBoardCards_PrintsThePinAndTheAgentWorkflow()
    {
        await _tool.CreateBoardCard("Pinned by hand", cancellationToken: Ct);
        await _tool.CreateBoardCard("Managed", workflowMode: "agent", managerSelection: "base:codex", cancellationToken: Ct);
        Assert.NotNull(await _service.SetCardPinnedAsync(_project, "PROJ-1", true, Ct));

        var lines = (await _tool.ListBoardCards(cancellationToken: Ct)).Split('\n');
        Assert.Contains(" — pinned", Assert.Single(lines, line => line.Contains("Pinned by hand")));
        var managed = Assert.Single(lines, line => line.Contains("Managed"));
        Assert.Contains(" — Agent workflow, manager base:codex", managed);
        Assert.DoesNotContain("pinned", managed);
    }

    [Fact]
    public async Task ListBoardColumns_NamesEachLaneEntryOnce_AndMarksTheDefaultLane()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Code reviewer", "", "codex", InitialPrompt: "Review"), Ct)).Preset;
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        var review = lanes.Single(lane => lane.Name == "Review");
        await _store.SaveLaunchLaneAsync(_project, review.Id, new([new(preset.Id, "preset", "review"), new("preset_gone", "preset")], 0), Ct);
        await _store.UpdateColumnAsync(_project, lanes[1].Id, null, null, Ct, true);

        var text = await _tool.ListBoardColumns(cancellationToken: Ct);

        // The name the lane panel shows, with the id kept for the launch tools, on one line.
        var entry = Assert.Single(text.Split('\n'), line => line.Contains(preset.Id));
        Assert.StartsWith("  on entry: \"Code reviewer\" — ", entry);
        Assert.Contains("id " + preset.Id, entry);
        // A definition that no longer resolves keeps its id as its name, still once.
        Assert.StartsWith("  on entry: \"preset_gone\" — ", Assert.Single(text.Split('\n'), line => line.Contains("preset_gone")));
        Assert.DoesNotContain("inspect launch catalog", text);

        Assert.Contains("default lane for new cards", Assert.Single(text.Split('\n'), line => line.StartsWith("- " + lanes[1].Name + " ")));
        Assert.DoesNotContain("default lane for new cards", Assert.Single(text.Split('\n'), line => line.StartsWith("- Review ")));
        // The next step is released by the run ending, not by a pass (BoardLaunchWorkflow.ReleasesNext).
        Assert.Contains("whatever its result", text);
        Assert.DoesNotContain("needs the preceding pass", text);
    }
}
