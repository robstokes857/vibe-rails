using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardServiceTests
{
    [Fact]
    public async Task DiscussionWorkRequiresExplicitDeclaration_AndLiveSessionAcrossAllCardViews()
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Discussion"), Ct);
        var other = await _service.CreateCardAsync(_project, new(Title: "Related"), Ct);
        var session = Guid.NewGuid().ToString();
        var original = await _store.LinkSessionAsync(_project, card.Id, session, "stale-tab", "base:codex", "codex", "Discussion", "chat", Ct);
        _live.Sessions[session] = "live-tab";

        await _service.AddCommentAsync(_project, card.Id, BoardAuthor.Agent("Codex", "codex", session), "Discussing", Ct);
        await _service.AttachSessionAsync(_project, card.Id, session, null, Ct);
        await AssertViews(null);
        Assert.True(Assert.Single((await _service.GetSessionsAsync(_project, card.Id, Ct))!).Active);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _service.AttachSessionAsync(_project, card.Id, session, null, Ct, work: true)));
        await AssertViews(session);
        var current = Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Sessions);
        Assert.Equal(original! with { Origin = BoardSessionRecord.ChatWorkOrigin }, current);
        Assert.Equal(card.Id, (await _store.FindSessionLinkAsync(session, Ct))!.CardId);

        // Work authorization belongs to the target card, not every related attachment.
        var related = await _service.AttachSessionAsync(_project, other.Id, session, null, Ct);
        Assert.Equal("chat", related!.Origin);
        Assert.Null((await _service.GetCardAsync(_project, other.Id, Ct))!.ActiveSessionId);

        // A declaration cannot manufacture liveness or keep a closed/replaced PTY running.
        _live.Sessions.Clear();
        _live.Sessions[Guid.NewGuid().ToString()] = "live-tab";
        await _service.AttachSessionAsync(_project, card.Id, session, null, Ct, work: true);
        await AssertViews(null);
        Assert.False(Assert.Single((await _service.GetSessionsAsync(_project, card.Id, Ct))!).Active);
        Assert.True(await _service.UnlinkSessionAsync(_project, card.Id, session, Ct));
        Assert.Equal(other.Id, (await _store.FindSessionLinkAsync(session, Ct))!.CardId);

        async Task AssertViews(string? expected)
        {
            var detail = (await _service.GetCardAsync(_project, card.Id, Ct))!;
            var list = (await _service.GetCardsAsync(_project, Ct)).Cards.Single(c => c.Id == card.Id);
            var page = (await _service.GetCardsPageAsync(_project, new(), Ct, card.BoardId)).Cards.Single(c => c.Id == card.Id);
            var activity = Assert.Single((await _service.GetCardActivityAsync(_project, new(card.BoardId, [card.Id]), Ct)).Cards);
            Assert.Equal(expected, detail.ActiveSessionId);
            Assert.Equal(expected, list.ActiveSessionId);
            Assert.Equal(expected, page.ActiveSessionId);
            Assert.Equal(expected, activity.ActiveSessionId);
            Assert.Equal(expected is null ? null : "live-tab", detail.ActiveTabId);
        }
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("code_review")]
    [InlineData("agent_manager")]
    [InlineData("agent_worker_planning")]
    [InlineData("automation")]
    public async Task WorkDeclarationNeverReclassifiesOtherPurposes(string origin)
    {
        var card = await _service.CreateCardAsync(_project, new(Title: "Protected purpose"), Ct);
        var session = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, session, "tab", "base:codex", "codex", "Agent", origin, Ct);
        Assert.Equal(origin, (await _service.AttachSessionAsync(_project, card.Id, session, null, Ct, work: true))!.Origin);
        Assert.False(await _store.AuthorizeDiscussionWorkAsync(_project + "-other", card.Id, session, Ct));
    }
}
