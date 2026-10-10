using VibeRails.Services.Board;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    private BoardTool DesktopTool(DesktopMcpActivityTracker tracker) => new(_service, _resolver, _store,
        new BoardWorkflowService(_store, new BoardReviewService(_store, _service)), desktopActivity: tracker);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DesktopCompletionRequiresExplicitCardAndNeverUsesInheritedTerminalAttribution(string? omittedCard)
    {
        await _tool.CreateBoardCard("Terminal work", cancellationToken: Ct);
        await _tool.CreateBoardCard("Desktop work", cancellationToken: Ct);
        var terminal = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var target = (await _store.FindCardAsync(_project, "PROJ-2", Ct))!;
        _resolver.CurrentSessionId = Guid.NewGuid().ToString("D");
        Assert.StartsWith("Attached session", await _tool.AttachBoardSession(terminal.Id, Ct));
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);
        await desktop.AttachBoardSession(target.Id, Ct);

        var rejected = await desktop.CompleteBoardAgent("Finished desktop work", card: omittedCard, cancellationToken: Ct);

        Assert.StartsWith("FAIL: Desktop MCP completion requires an explicit card", rejected);
        Assert.Contains(target.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [target.Id], DateTime.UtcNow, Ct));
        Assert.DoesNotContain((await _store.GetCardDetailAsync(_project, terminal.Id, Ct))!.Comments,
            comment => comment.Body.Contains("Finished desktop work"));
        Assert.DoesNotContain((await _store.GetCardDetailAsync(_project, target.Id, Ct))!.Comments,
            comment => comment.Body.Contains("Finished desktop work"));

        await desktop.AddBoardComment("Desktop checkpoint", target.Id, Ct, syncToJira: false);
        Assert.Contains("activity ended", await desktop.CompleteBoardAgent("Finished desktop work", card: target.Id, cancellationToken: Ct));
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [target.Id], DateTime.UtcNow, Ct));
        var detail = (await _store.GetCardDetailAsync(_project, target.Id, Ct))!;
        Assert.Empty(detail.Sessions);
        Assert.Null(Assert.Single(detail.Comments, comment => comment.Body.Contains("Finished desktop work")).Author.SessionId);
        Assert.Null(Assert.Single(detail.Comments, comment => comment.Body == "Desktop checkpoint").Author.SessionId);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("attachment")]
    [InlineData("commit")]
    [InlineData("handoff")]
    public async Task DesktopSuccessfulCardWritesMarkActivity(string operation)
    {
        await _tool.CreateBoardCard("Desktop work", cancellationToken: Ct);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var working = await _store.CreateColumnAsync(_project, "Desktop working", "#999999", Ct);
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);

        var result = operation switch
        {
            "move" => await desktop.MoveBoardCard(card.Id, working.Id, skipAutomations: true, cancellationToken: Ct),
            "attachment" => await desktop.AddBoardAttachment("work.md", "Working", card.Id, Ct),
            "commit" => await desktop.LinkBoardCommit("abc1234", card.Id, Ct),
            "handoff" => await desktop.SaveBoardHandoff(new BoardHandoff("Working", "", "Checked", "", []), card.Id, Ct),
            _ => throw new InvalidOperationException()
        };

        Assert.DoesNotContain("FAIL:", result);
        Assert.Contains(card.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Sessions);
    }

    [Fact]
    public async Task DesktopMovePreviewAndFailedWritesDoNotStartActivityAndClosingEndsIt()
    {
        await _tool.CreateBoardCard("Desktop work", cancellationToken: Ct);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var working = await _store.CreateColumnAsync(_project, "Desktop working", "#999999", Ct);
        var closed = await _store.CreateColumnAsync(_project, "Closed", "#999999", Ct);
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);

        Assert.StartsWith("Preview:", await desktop.MoveBoardCard(card.Id, working.Id, preview: true, cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await desktop.AddBoardAttachment("work.exe", "Invalid", card.Id, Ct));
        Assert.StartsWith("FAIL:", await desktop.LinkBoardCommit("nope", card.Id, Ct));
        Assert.StartsWith("FAIL:", await desktop.SaveBoardHandoff(new BoardHandoff("", "", "", "", []), card.Id, Ct));
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
        await desktop.MoveBoardCard(card.Id, working.Id, skipAutomations: true, cancellationToken: Ct);
        await desktop.MoveBoardCard(card.Id, closed.Id, skipAutomations: true, cancellationToken: Ct);
        await tracker.RenewAsync(Ct);
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task DesktopCommitDoesNotUseAnInheritedSessionToWriteOtherCards()
    {
        await _tool.CreateBoardCard("Desktop target", cancellationToken: Ct);
        await _tool.CreateBoardCard("Terminal work", cancellationToken: Ct);
        var target = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var terminal = (await _store.FindCardAsync(_project, "PROJ-2", Ct))!;
        _resolver.CurrentSessionId = Guid.NewGuid().ToString("D");
        Assert.StartsWith("Attached session", await _tool.AttachBoardSession(terminal.Id, Ct));
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);

        var result = await DesktopTool(tracker).LinkBoardCommit("abc1234", target.Id, Ct);

        Assert.StartsWith("Linked abc1234", result);
        Assert.DoesNotContain("Also linked", result);
        Assert.Single((await _store.GetCardDetailAsync(_project, target.Id, Ct))!.Commits);
        Assert.Empty((await _store.GetCardDetailAsync(_project, terminal.Id, Ct))!.Commits);
        Assert.Empty((await _store.GetCardDetailAsync(_project, target.Id, Ct))!.Sessions);
        Assert.DoesNotContain(terminal.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [target.Id, terminal.Id], DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task DesktopWithInheritedSessionIdNeverAutoLinksATerminal()
    {
        _resolver.CurrentSessionId = Guid.NewGuid().ToString("D");
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);

        Assert.DoesNotContain("FAIL:", await desktop.CreateBoardCard("Desktop with inherited identity", cancellationToken: Ct));
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.DoesNotContain("FAIL:", await desktop.UpdateBoardCard(card.Id, title: "Updated from desktop", cancellationToken: Ct));
        Assert.DoesNotContain("FAIL:", await desktop.AddBoardComment("Working in the app", card.Id, Ct, syncToJira: false));
        Assert.Contains("Linked desktop activity", await desktop.AttachBoardSession(card.Id, Ct));

        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Empty(detail.Sessions);
        Assert.Null(await _store.FindSessionLinkAsync(_resolver.CurrentSessionId, Ct));
        Assert.Contains(card.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task DesktopReadsDoNotStartActivityButAttachAndCompletionWorkWithoutATerminal()
    {
        await _tool.CreateBoardCard("Desktop work", cancellationToken: Ct);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);
        await desktop.GetBoardCard(card.Id, cancellationToken: Ct);
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
        Assert.Contains("Linked desktop activity", await desktop.AttachBoardSession(card.Id, Ct));
        Assert.Contains(card.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
        Assert.Contains("Desktop app activity:", await desktop.GetBoardCard(card.Id, cancellationToken: Ct));
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Sessions);
        Assert.Contains("activity ended", await desktop.CompleteBoardAgent("Implemented and tested", card: card.Id, cancellationToken: Ct));
        await tracker.RenewAsync(Ct);
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [card.Id], DateTime.UtcNow, Ct));
        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Empty(detail.Sessions);
        Assert.Contains(detail.Comments, comment => comment.Body.Contains("Desktop agent reported succeeded: Implemented and tested"));
    }

    [Fact]
    public async Task DesktopWritesStartActivityButForeignWritesAndFailedWritesDoNot()
    {
        using var tracker = new DesktopMcpActivityTracker(_store, true, _ => null, TimeProvider.System);
        var desktop = DesktopTool(tracker);
        await desktop.CreateBoardCard("Created in desktop", cancellationToken: Ct);
        var created = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Contains(created.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [created.Id], DateTime.UtcNow, Ct));
        await tracker.EndAsync(null, created.Id, "Agent", Ct);
        await desktop.UpdateBoardCard(created.Id, title: "Updated in desktop", cancellationToken: Ct);
        Assert.Contains(created.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [created.Id], DateTime.UtcNow, Ct));
        await tracker.EndAsync(null, created.Id, "Agent", Ct);
        Assert.StartsWith("FAIL:", await desktop.UpdateBoardCard(created.Id, priority: "invalid", cancellationToken: Ct));
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [created.Id], DateTime.UtcNow, Ct));
        await desktop.AddBoardComment("Working", created.Id, Ct, syncToJira: false);
        Assert.Contains(created.Id, await _store.GetDesktopActiveCardIdsAsync(_project, [created.Id], DateTime.UtcNow, Ct));

        var foreignProject = _project + "-foreign";
        await _store.EnsureDefaultColumnsAsync(foreignProject, Ct);
        var foreign = await _store.CreateCardAsync(foreignProject, new(null, "Foreign", "", null, "medium", null, [], false), Ct);
        Assert.DoesNotContain("FAIL:", await desktop.AddBoardComment("Read and commented", foreign.Id, Ct, syncToJira: false));
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(foreignProject, [foreign.Id], DateTime.UtcNow, Ct));
        Assert.StartsWith("FAIL: session attachments must stay", await desktop.AttachBoardSession(foreign.Id, Ct));
    }
}
