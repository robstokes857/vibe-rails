using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task AttachDiscussionExplainsExplicitWork_AndDoesNotClaimAnUnconfirmedExit()
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Discuss then work"), Ct);
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, "tab", "base:codex", "codex", "Discussion", "chat", Ct);
        Assert.Contains("This remains a discussion", await _tool.AttachBoardSession(card.Id, Ct));
        var text = await _tool.GetBoardCard(card.Id, cancellationToken: Ct);
        Assert.Contains("status unknown (no confirmed end time)", text);
        Assert.DoesNotContain(" · ended", text);
        Assert.Contains("purpose: chat", text);

        Assert.Contains("declared as authorized work", await _tool.AttachBoardSession(card.Id, Ct, work: true));
        var attached = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Sessions);
        Assert.Equal(BoardSessionRecord.ChatWorkOrigin, attached.Origin);
        Assert.Contains("purpose: chat_work", await _tool.GetBoardCard(card.Id, cancellationToken: Ct));
        Assert.Contains("declared as authorized work", await _tool.AttachBoardSession(card.Id, Ct, work: true));
    }
}
