using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sharing;
using Xunit;

namespace Tests.Services.Board;

public sealed class CardShareCaptureTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IBoardStore> _boards = new();
    private readonly Mock<ISessionStore> _sessions = new();
    private readonly Mock<IChatSummaryStore> _summaries = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static BoardCardRecord Card() => new("card_test", "project", 1, "lane", 0, "Complete card", "Description",
        null, "medium", 3, ["feature"], false, 1, Now, Now, BoardId: "board", StoredKey: "VB-CARDS-123");

    private CardShareCapture SetUp(BoardCardDetailRecord detail, IReadOnlyList<BoardAttachmentMetadata>? attachments = null)
    {
        _boards.Setup(b => b.FindCardAsync("project", "card_test", It.IsAny<CancellationToken>())).ReturnsAsync(detail.Card);
        _boards.Setup(b => b.GetSyncActivityAsync("project", "board", "card_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardSyncActivityRecord(detail.Sessions.Take(201).ToList(), detail.Commits.Take(201).ToList(), attachments ?? [], []));
        _boards.Setup(b => b.GetCardDetailAsync("project", "card_test", It.IsAny<CancellationToken>())).ReturnsAsync(detail);
        _boards.Setup(b => b.GetCardHistoryAsync("project", "card_test", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _boards.Setup(b => b.GetReviewsAsync("project", "card_test", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _boards.Setup(b => b.GetChecksAsync("project", "card_test", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _summaries.Setup(s => s.GetChatSummariesBySessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new(_boards.Object, _sessions.Object, _summaries.Object);
    }
    [Fact]
    public async Task CapturesAllLinkedSessionsLargeDocumentsAndSavedCommitContentWithoutHostedSyncTruncation()
    {
        var sessions = Enumerable.Range(0, 205).Select(i => new BoardSessionRecord(Guid.NewGuid().ToString("D"), "card_test", null,
            "base:codex", "codex", "Session " + i, "launch", Now)).ToList();
        var document = new byte[2 * 1024 * 1024]; Array.Fill(document, (byte)'x');
        var attachment = new BoardAttachmentRecord("att_test", "card_test", "design.md", "text/markdown", document.Length, "", Now);
        var detail = new BoardCardDetailRecord(Card(), [new("comment", "card_test", BoardAuthor.User(), "Notes", Now)], sessions,
            [attachment], [new("card_test", "abcdef0123456", "Author", "Message", Now, Now)], []);
        var capture = SetUp(detail, [new("att_test", "card_test", attachment.Name, attachment.MimeType, attachment.Bytes, Now)]);
        _boards.Setup(b => b.GetAttachmentContentAsync("project", "card_test", "att_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardAttachmentContent(attachment, document));
        var code = new string('x', 300000);
        _boards.Setup(b => b.GetCommitSnapshotAsync("project", "card_test", "abcdef0123456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxDiffResponse([new("app.cs", "csharp", "before", code)], 1));
        _summaries.Setup(s => s.GetChatSummariesBySessionAsync(sessions[0].SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ChatSummary { SummaryText = new string('s', 20000), Date = Now }]);
        var snapshot = await capture.CaptureAsync("project", "card_test", Ct);
        Assert.Equal(205, snapshot.Sessions.Count);
        Assert.Equal(20000, snapshot.Sessions[0].Summary!.Length);
        Assert.Equal(document, Convert.FromBase64String(Assert.Single(snapshot.Attachments).ContentBase64));
        Assert.Equal(code, Assert.Single(Assert.Single(snapshot.Commits).Files).After);
        Assert.Equal("Notes", Assert.Single(snapshot.Discussion).Body);
    }
    [Fact]
    public async Task HistoryPreservesFullFieldValuesWithoutLocalLaunchConfiguration()
    {
        var detail = new BoardCardDetailRecord(Card() with { Assignee = "env:123:codex" }, [], [], [], [], []);
        var capture = SetUp(detail);
        var description = new string('d', 100000);
        var changes = System.Text.Json.JsonSerializer.Serialize(new
        {
            description = new { to = description },
            title = new { from = "Old title", to = "New title" },
            assignee = new { from = "env:456:claude", to = "env:123:codex" },
            launchPath = new { to = "PRIVATE_LOCAL_CONFIGURATION" }
        });
        _boards.Setup(b => b.GetCardHistoryAsync("project", "card_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("change", "card_test", BoardAuthor.User(), "Assignee: env:456:claude → env:123:codex", Now,
                BoardCommentKinds.Change, changes)]);
        var snapshot = await capture.CaptureAsync("project", "card_test", Ct);
        Assert.Equal("base:codex", snapshot.Assignee);
        var history = Assert.Single(snapshot.History).Body;
        Assert.Contains(description, history);
        Assert.Contains("from: Old title\nto: New title", history);
        Assert.Contains("from: base:claude\nto: base:codex", history);
        Assert.DoesNotContain("env:", history);
        Assert.DoesNotContain("PRIVATE_LOCAL_CONFIGURATION", history);
    }
    [Fact]
    public async Task RejectsOversizedAttachmentBeforeLoadingCardBodies()
    {
        var detail = new BoardCardDetailRecord(Card(), [], [], [], [], []);
        var capture = SetUp(detail, [new("att_large", "card_test", "large.bin", "application/octet-stream", 100_000_000, Now)]);
        await Assert.ThrowsAsync<BoardValidationException>(() => capture.CaptureAsync("project", "card_test", Ct));
        _boards.Verify(b => b.GetCardDetailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _boards.Verify(b => b.GetAttachmentContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task PagesAllSavedReviewReports()
    {
        var detail = new BoardCardDetailRecord(Card(), [], [], [], [], []);
        var capture = SetUp(detail);
        var reviews = Enumerable.Range(0, 51).Select(i => new BoardReviewRecord("review_" + i, "card_test", "codex", "Reviewer", Now,
            Result: "No findings reported", Findings: "Findings " + i, Validation: "Validated")).ToList();
        _boards.Setup(b => b.GetReviewsAsync("project", "card_test", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string c, int offset, CancellationToken token) => reviews.Skip(offset).Take(50).ToList());
        _boards.Setup(b => b.GetReviewAsync("project", "card_test", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string c, string id, CancellationToken token) => reviews.Single(r => r.Id == id));
        var snapshot = await capture.CaptureAsync("project", "card_test", Ct);
        Assert.Equal(51, snapshot.Evidence.Count);
        Assert.Contains("Findings 50", snapshot.Evidence[^1].Body);
    }
    [Fact]
    public async Task DeletedCardDuringCaptureCannotBePublished()
    {
        var detail = new BoardCardDetailRecord(Card(), [], [], [], [], []);
        var capture = SetUp(detail);
        _boards.SetupSequence(b => b.FindCardAsync("project", "card_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail.Card).ReturnsAsync((BoardCardRecord?)null);
        await Assert.ThrowsAsync<BoardValidationException>(() => capture.CaptureAsync("project", "card_test", Ct));
    }
}
