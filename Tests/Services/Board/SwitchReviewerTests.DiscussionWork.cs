using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class SwitchReviewerTests
{
    [Fact]
    public async Task AuthorizedDiscussionCanBeExplicitCodingSource_WithoutChangingItsLaunchHistory()
    {
        var session = await Session("claude", "chat");
        var history = await boards.RecordContextSampleAsync(project, card.Id,
            new(session, "chat", "claude", "base:claude", 0, 0, 0, 0, "{}"), Ct);
        var settings = new BoardReviewSettings("session", session, "Implemented after explicit authorization");
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, settings, Ct));
        var service = new BoardService(boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        await service.AttachSessionAsync(project, card.Id, session, null, Ct, work: true);
        Assert.False(await boards.IsNonCodingSessionAsync(project, session, Ct));
        Assert.Equal(history, await boards.GetLatestContextSampleAsync(project, card.Id, Ct));
        Assert.NotNull(await routing.SaveSettingsAsync(project, card.Id, settings, Ct));
        Assert.Equal("claude", (await Resolve()).Resolution.Source.Provider);

        // Unrelated planning/review provenance is never overridden by a work declaration.
        await boards.RecordContextSampleAsync(project, card.Id,
            new(session, "planning", "claude", "base:claude", 0, 0, 0, 0, "{}"), Ct);
        Assert.True(await boards.IsNonCodingSessionAsync(project, session, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, settings, Ct));
    }
}
