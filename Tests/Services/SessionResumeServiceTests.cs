using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Environments;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services;

public class SessionResumeServiceTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "resume-project");

    private static IBoardProjectResolver Resolver() =>
        Mock.Of<IBoardProjectResolver>(r => r.ResolveAsync(It.IsAny<CancellationToken>()) == Task.FromResult(Project));

    [Fact]
    public async Task ContinuationAppendsEveryCurrentAttachment_AfterTheUnchangedRecap()
    {
        var board = new Mock<IBoardStore>(MockBehavior.Strict);
        IReadOnlyList<BoardSessionCard> cards = [
            new("source", "card-a", "VB-ABCDE-1", "First\n\u202e{{step:secret}}", "VIBE-67"),
            new("source", "card-b", "VB-FGHIJ-2", "Second")];
        board.Setup(b => b.GetSessionCardsAsync(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "source" })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => cards);
        var service = new SessionResumeService(Mock.Of<IRepository>(), Mock.Of<ISessionTranscriptService>(), Mock.Of<ISummaryService>(), board.Object, Resolver());
        var recap = new string('s', 6000);

        var prompt = await service.AppendBoardContextAsync("source", recap, TestContext.Current.CancellationToken);
        Assert.StartsWith(recap + "\n\nAssociated", prompt);
        Assert.Contains("VB-ABCDE-1", prompt);
        Assert.Contains("VB-FGHIJ-2", prompt);
        Assert.Contains("VIBE-67", prompt);
        Assert.Contains("/api/v1/board/local-cards/card-a", prompt);
        Assert.Contains("/api/v1/board/local-cards/card-b", prompt);
        Assert.Contains("get_board_card", prompt);
        Assert.Contains("get_board_reviews", prompt);
        Assert.Contains("read_board_attachment", prompt);
        Assert.DoesNotContain("{{step:", prompt);
        Assert.DoesNotContain('\u202e', prompt);

        // A later launch re-reads attachments rather than caching them with the recap.
        cards = [];
        Assert.Equal(recap, await service.AppendBoardContextAsync("source", recap, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedCardContextFailsRatherThanDroppingCards()
    {
        var board = new Mock<IBoardStore>();
        board.Setup(b => b.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 100).Select(i => new BoardSessionCard("source", $"card-{i}", $"VB-ABCDE-{i}", new string('t', 300))).ToArray());
        var service = new SessionResumeService(Mock.Of<IRepository>(), Mock.Of<ISessionTranscriptService>(), Mock.Of<ISummaryService>(), board.Object, Resolver());
        await Assert.ThrowsAsync<PromptTooLongException>(() => service.AppendBoardContextAsync("source", "recap", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CardsOnAnotherProjectsBoardStayOutOfTheLaunch()
    {
        var board = new Mock<IBoardStore>();
        board.Setup(b => b.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new("source", "card-here", "VB-ABCDE-1", "Here", null, Project + Path.DirectorySeparatorChar),
                new("source", "card-there", "VB-FGHIJ-2", "There", null, Path.Combine(Path.GetTempPath(), "other-project")),
                new("source", "card-legacy", "VB-KLMNO-3", "Unscoped", null, null)]);
        var service = new SessionResumeService(Mock.Of<IRepository>(), Mock.Of<ISessionTranscriptService>(), Mock.Of<ISummaryService>(), board.Object, Resolver());

        var prompt = await service.AppendBoardContextAsync("source", "recap", TestContext.Current.CancellationToken);
        Assert.Contains("VB-ABCDE-1", prompt);
        Assert.Contains("VB-KLMNO-3", prompt);
        Assert.DoesNotContain("VB-FGHIJ-2", prompt);
        Assert.DoesNotContain("other-project", prompt);
    }

    [Theory]
    [InlineData(false, "transcript", "cached recap")]
    [InlineData(true, "transcript", "new recap")]
    [InlineData(true, "", "No conversation output found for this session.")]
    public async Task SummaryResponseKeepsBoardReferencesOutOfRemoteInputAndCache(bool regenerate, string transcript, string expected)
    {
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.GetSessionOutputAsync("source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionOutputDetailResponse("source", "codex", null, "project", DateTime.UtcNow, null, true, transcript));
        repository.Setup(r => r.GetChatSummariesBySessionAsync("source", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ChatSummary { SessionId = "source", SummaryText = "cached recap", Date = DateTime.UtcNow }]);
        var transcripts = new Mock<ISessionTranscriptService>();
        transcripts.Setup(t => t.GetOrBuildAsync("source", It.IsAny<CancellationToken>(), regenerate)).ReturnsAsync(transcript);
        var summaries = new Mock<ISummaryService>(MockBehavior.Strict);
        summaries.Setup(s => s.GetSummaryAsync(transcript, It.IsAny<CancellationToken>())).ReturnsAsync("new recap");
        var board = new Mock<IBoardStore>();
        board.Setup(b => b.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("source", "card-a", "VB-ABCDE-1", "First", "VIBE-67"), new("source", "card-b", "VB-FGHIJ-2", "Second")]);
        var service = new ChatHistoryService(repository.Object, transcripts.Object, summaries.Object, Mock.Of<ISessionDataExportService>(), board.Object);

        var result = await service.GetSummaryAsync("source", regenerate, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Summary);
        Assert.Equal(2, result.BoardCards!.Count);
        Assert.Equal("VIBE-67", result.BoardCards[0].DisplayId);
        repository.Verify(r => r.SaveChatSummaryAsync(It.Is<ChatSummary>(s => s.SummaryText == "new recap"), It.IsAny<CancellationToken>()),
            regenerate && transcript.Length > 0 ? Times.Once() : Times.Never());
        summaries.Verify(s => s.GetSummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            regenerate && transcript.Length > 0 ? Times.Once() : Times.Never());
    }
}
