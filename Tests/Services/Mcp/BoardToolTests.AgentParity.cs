using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tests.Services.BertV2;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// What a person can set or read on a card in the dashboard, an agent can set or read over MCP:
/// the pin, the display ID, points and blocked at creation, a linked commit's saved diff, the
/// dashboard search's filters, and a run against a card.
/// </summary>
public sealed partial class BoardToolTests
{
    [Fact]
    public async Task UpdateBoardCard_PinsWithoutEditingTheCard_AndGetBoardCardShowsThePin()
    {
        await _tool.CreateBoardCard("Keep me here", cancellationToken: Ct);
        var before = (await _service.GetCardAsync(_project, "PROJ-1", Ct))!;

        Assert.StartsWith("Pinned ", await _tool.UpdateBoardCard("PROJ-1", pinned: true, cancellationToken: Ct));
        var pinned = (await _service.GetCardAsync(_project, "PROJ-1", Ct))!;
        Assert.True(pinned.Pinned);
        // A pin is not an edit: nothing else about the card moved.
        Assert.Equal(before.UpdatedAt, pinned.UpdatedAt);
        Assert.Equal(before.CommentCount, pinned.CommentCount);
        Assert.Contains(" · Pinned", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));

        // Beside other fields the pin travels with the update, and the reply says so.
        var both = await _tool.UpdateBoardCard("PROJ-1", priority: "high", pinned: false, cancellationToken: Ct);
        Assert.StartsWith("Updated ", both);
        Assert.DoesNotContain("pinned", both);
        Assert.False((await _service.GetCardAsync(_project, "PROJ-1", Ct))!.Pinned);
        Assert.DoesNotContain(" · Pinned", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.UpdateBoardCard("PROJ-99", pinned: true, cancellationToken: Ct));
    }

    [Fact]
    public async Task UpdateBoardCard_SetsTheDisplayId_AndCreateTakesPointsAndBlocked()
    {
        Assert.StartsWith("Created ", await _tool.CreateBoardCard("Sized and waiting", points: 5, blocked: true, cancellationToken: Ct));
        var card = (await _service.GetCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal(5, card.Points);
        Assert.True(card.Blocked);
        var read = await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct);
        Assert.Contains(" · Points: 5", read);
        Assert.Contains(" · BLOCKED", read);
        Assert.StartsWith("FAIL: Points must be one of", await _tool.CreateBoardCard("Odd size", points: 4, cancellationToken: Ct));

        var renamed = await _tool.UpdateBoardCard("PROJ-1", displayId: "SHOP-7", cancellationToken: Ct);
        Assert.Contains("Display ID: SHOP-7", renamed);
        var found = (await _service.GetCardAsync(_project, "SHOP-7", Ct))!;
        Assert.Equal(card.Id, found.Id);
        // The permanent key is the identity and never changes.
        Assert.Equal(card.Key, found.Key);
    }

    [Fact]
    public async Task ReadBoardCommit_ListsTheSavedFiles_AndPagesOneFile()
    {
        const string sha = "feedfacefeedfacefeedfacefeedfacefeedface";
        var after = "line one\n" + new string('x', 300) + "\nlast line\n";
        var commits = new Mock<IBoardCommitService>(MockBehavior.Strict);
        commits.Setup(c => c.DescribeAsync(_project, "feedfac", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo(sha, "Rob", "Quote totals", DateTime.UtcNow));
        commits.Setup(c => c.GetDiffAsync(_project, sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([
                new("src/cart/totals.ts", "typescript", "old body\n", after),
                new("src/pricing/PricingClient.ts", "typescript", "", "export const pricing = {};\n"),
                new("src/legacy.ts", "typescript", "gone\n", "")], 3));
        var service = new BoardService(_store, commits.Object, new NullBoardLiveSessionProbe());
        var tool = new BoardTool(service, _resolver, _store, new BoardWorkflowService());
        await tool.CreateBoardCard("Totals", cancellationToken: Ct);
        Assert.StartsWith("Linked", await tool.LinkBoardCommit("feedfac", "PROJ-1", Ct));

        var list = await tool.ReadBoardCommit("feedfac", "PROJ-1", cancellationToken: Ct);
        Assert.Contains("3 files", list);
        Assert.Contains("- src/cart/totals.ts · modified · before 9 chars · after " + after.Length + " chars", list);
        Assert.Contains("- src/pricing/PricingClient.ts · added · before 0 chars", list);
        Assert.Contains("- src/legacy.ts · deleted", list);

        var both = await tool.ReadBoardCommit(sha, "PROJ-1", "src/cart/totals.ts", cancellationToken: Ct);
        Assert.Contains("--- Before: characters 0–9 of 9\nold body\n", both);
        Assert.Contains($"--- After: characters 0–{after.Length} of {after.Length}\nline one\n", both);

        // A long side is read in chunks, and the reply says where the next one starts.
        var first = await tool.ReadBoardCommit("feedfac", "PROJ-1", "SRC/cart/totals.ts", side: "after", maxCharacters: 100, cancellationToken: Ct);
        Assert.DoesNotContain("--- Before", first);
        Assert.Contains($"--- After: characters 0–100 of {after.Length} (continue with offset=100)", first);
        var rest = await tool.ReadBoardCommit("feedfac", "PROJ-1", "src/cart/totals.ts", side: "after", offset: 100, maxCharacters: 250_000, cancellationToken: Ct);
        Assert.Contains($"characters 100–{after.Length} of {after.Length}\n", rest);
        Assert.EndsWith("last line", rest);
        Assert.Contains("--- Before: empty", await tool.ReadBoardCommit("feedfac", "PROJ-1", "src/pricing/PricingClient.ts", cancellationToken: Ct));

        Assert.StartsWith("FAIL: that file is not in this commit's saved snapshot", await tool.ReadBoardCommit("feedfac", "PROJ-1", "nope.ts", cancellationToken: Ct));
        Assert.StartsWith("FAIL: sha must be 7 to 40 hex", await tool.ReadBoardCommit("xyz", "PROJ-1", cancellationToken: Ct));
        Assert.StartsWith("FAIL: side must be", await tool.ReadBoardCommit("feedfac", "PROJ-1", "src/legacy.ts", side: "diff", cancellationToken: Ct));
        Assert.StartsWith("FAIL: That commit is not linked to this card", await tool.ReadBoardCommit("0123456", "PROJ-1", cancellationToken: Ct));
        // Reading is not activity: the session is not attached to the card by it.
        _resolver.CurrentSessionId = "reader-only";
        await tool.ReadBoardCommit("feedfac", "PROJ-1", cancellationToken: Ct);
        Assert.Null(await _store.FindSessionLinkAsync("reader-only", Ct));
    }

    [Fact]
    public async Task SearchBoardCards_AppliesTheDashboardFilters()
    {
        await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Checkout retrieval bug", Type: "bug", Priority: "critical", Assignee: "base:claude"), Ct);
        await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Checkout retrieval chore", Type: "chore", Priority: "low", Assignee: "base:codex"), Ct);
        var search = new BoardSearchService(SearchTestIndex.Build(_store, _project), () => throw new IOException("Model unavailable"), NullLogger<BoardSearchService>.Instance);
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(), search: search);

        var all = await tool.SearchBoardCards("retrieval", cancellationToken: Ct);
        Assert.Contains("Checkout retrieval bug", all);
        Assert.Contains("Checkout retrieval chore", all);

        foreach (var filtered in new[]
        {
            await tool.SearchBoardCards("retrieval", priority: "critical", cancellationToken: Ct),
            await tool.SearchBoardCards("retrieval", type: "bug", cancellationToken: Ct),
            // The same shorthand create and update take.
            await tool.SearchBoardCards("retrieval", assignee: "claude", cancellationToken: Ct)
        })
        {
            Assert.Contains("Checkout retrieval bug", filtered);
            Assert.DoesNotContain("Checkout retrieval chore", filtered);
        }
        Assert.Equal("No cards match that query with those filters across the local boards.",
            await tool.SearchBoardCards("retrieval", priority: "medium", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Priority must be one of", await tool.SearchBoardCards("retrieval", priority: "urgent", cancellationToken: Ct));
    }

    [Fact]
    public async Task AgentStatus_LabelsTheEventKey_AndNamesTheStep()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Code reviewer", "", "codex", InitialPrompt: "Review"), Ct)).Preset;
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.SaveLaunchLaneAsync(_project, lanes[0].Id, new([new(preset.Id, "preset", "review")], 0), Ct);
        var card = await _store.CreateCardAsync(_project, new(lanes[0].Id, "Workflow", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await _store.GetLaunchEventsAsync(_project, card.Id, Ct));

        var status = await _tool.GetBoardAgentStatus(card.Key, cancellationToken: Ct);

        // report_automation_step and skip_lane_step ask for "eventKey"; the reply now names it.
        Assert.Contains($"- Lane entry · eventKey {entry.Id} · \"Code reviewer\" ({preset.Id}) · current: True", status);
    }

    [Fact]
    public async Task RunLaunch_WithACard_QueuesTheCardsRun_LikeTheEditorsRunButton()
    {
        var preset = (await _launchService.SavePresetAsync(null, new("Docs updater", "", "codex", InitialPrompt: "Update the docs"), Ct)).Preset;
        await _tool.CreateBoardCard("Document the pricing client", cancellationToken: Ct);
        var card = (await _service.GetCardAsync(_project, "PROJ-1", Ct))!;
        var reviews = new BoardReviewService(_store, _service, _launches);
        var workflow = new VibeRails.Services.Launches.BoardLaunchWorkflow(_store, _launches, _launchService, reviews);
        var launchTool = new LaunchTool(_launchService, _launches, _resolver, boardRuns: workflow);

        var run = await launchTool.RunLaunch(preset.Id, occurrenceId: "agent-run-1", ct: Ct, card: "PROJ-1");

        Assert.Equal(card.Id, run.Snapshot.Context.CardId);
        Assert.Equal(card.BoardId, run.Snapshot.Context.BoardId);
        Assert.Contains(card.Key, run.Snapshot.Context.ComposedPrompt);
        // The card's rail reads its runs from here.
        Assert.Contains(await _launches.GetBoardRunsAsync(VibeRails.Services.Launches.LaunchService.ProjectKey(_project), card.Id, ct: Ct), item => item.Id == run.Id);
        // The same occurrence is one run, as for the editor's button.
        Assert.Equal(run.Id, (await launchTool.RunLaunch(preset.Id, occurrenceId: "agent-run-1", ct: Ct, card: card.Id)).Id);
        await Assert.ThrowsAsync<VibeRails.Launches.LaunchValidationException>(() => launchTool.RunLaunch(preset.Id, ct: Ct, card: "PROJ-404"));
        // Without a card the tool behaves as before: a project run with no card context.
        Assert.Null((await launchTool.RunLaunch(preset.Id, ct: Ct)).Snapshot.Context.CardId);
        await Assert.ThrowsAsync<VibeRails.Launches.LaunchValidationException>(() => new LaunchTool(_launchService, _launches, _resolver).RunLaunch(preset.Id, ct: Ct, card: "PROJ-1"));
    }
}
