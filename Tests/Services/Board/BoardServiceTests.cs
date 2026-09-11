using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>Validation and wire mapping over a real store; git and the tab host are faked.</summary>
public sealed class BoardServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-service-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly Mock<IBoardCommitService> _commits = new(MockBehavior.Strict);
    private readonly FakeLiveProbe _live = new();
    private readonly BoardStore _store;
    private readonly BoardService _service;

    public BoardServiceTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString);
        _service = new BoardService(_store, _commits.Object, _live);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetColumns_SeedsDefaults_AndCreateCardValidates()
    {
        var columns = await _service.GetColumnsAsync(_project, Ct);
        Assert.Equal(5, columns.Columns.Count);

        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "   "), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "x", Priority: "urgent"), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "x", Points: Element("4")), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "x", Assignee: "maya"), Ct));

        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(
            Title: "  Ship it  ",
            Assignee: "env:7:Codex",
            Priority: "High",
            Points: Element("\"5\""),
            Tags: ["auth", " auth ", "", "bug"]), Ct);
        Assert.Equal("Ship it", created.Title);
        Assert.Equal("env:7:codex", created.Assignee);
        Assert.Equal("high", created.Priority);
        Assert.Equal(5, created.Points);
        Assert.Equal(["auth", "bug"], created.Tags);
        Assert.Equal("VB-1", created.Key);
        Assert.Empty(created.Comments);
        Assert.Empty(created.Sessions);
    }

    [Fact]
    public async Task UpdateCard_DistinguishesOmitted_Null_AndEmpty()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(
            Title: "A", Assignee: "base:claude", Points: Element("3"), Tags: ["x"]), Ct);

        // Omitted fields stay as they are.
        var untouched = await _service.UpdateCardAsync(_project, created.Key, new UpdateBoardCardRequest(Description: "more"), Ct);
        Assert.Equal("base:claude", untouched!.Assignee);
        Assert.Equal(3, untouched.Points);
        Assert.Equal(["x"], untouched.Tags);

        // "" clears points and the assignee; [] clears tags; null points also clears.
        var cleared = await _service.UpdateCardAsync(_project, created.Id, new UpdateBoardCardRequest(
            Assignee: "", Points: Element("\"\""), Tags: []), Ct);
        Assert.Null(cleared!.Assignee);
        Assert.Null(cleared.Points);
        Assert.Empty(cleared.Tags);

        var nulled = await _service.UpdateCardAsync(_project, created.Id, new UpdateBoardCardRequest(Points: Element("8")), Ct);
        Assert.Equal(8, nulled!.Points);
        nulled = await _service.UpdateCardAsync(_project, created.Id, new UpdateBoardCardRequest(Points: Element("null")), Ct);
        Assert.Null(nulled!.Points);

        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.UpdateCardAsync(_project, created.Id, new UpdateBoardCardRequest(Title: ""), Ct));
        Assert.Null(await _service.UpdateCardAsync(_project, "VB-999", new UpdateBoardCardRequest(Title: "x"), Ct));
    }

    [Fact]
    public async Task Move_ResolvesLanesByName_AndRejectsUnknown()
    {
        var columns = await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        var moved = await _service.MoveCardAsync(_project, created.Key, "review", null, Ct);
        Assert.Equal(columns.Columns.Single(c => c.Name == "Review").Id, moved!.ColumnId);
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.MoveCardAsync(_project, created.Key, "Nope", null, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.MoveCardAsync(_project, created.Key, "Build", -1, Ct));
    }

    [Fact]
    public async Task Attachments_MustBeImages_AndAreCapped()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.AddAttachmentAsync(_project, created.Id, new AddBoardAttachmentRequest("x", "data:text/html;base64,AAAA"), Ct));
        var ok = await _service.AddAttachmentAsync(_project, created.Id, new AddBoardAttachmentRequest("shot.png", "data:image/png;base64,AAAA", 4, "image/png"), Ct);
        Assert.Equal("data:image/png;base64,AAAA", ok!.Url);
        Assert.Equal("image/png", ok.MimeType);

        var detail = await _service.GetCardAsync(_project, created.Id, Ct);
        Assert.Single(detail!.Attachments);
        Assert.True(await _service.DeleteAttachmentAsync(_project, created.Id, ok.Id, Ct));
    }

    [Fact]
    public async Task LinkCommit_UsesGitMetadata_AndDiffMapsToTheSandboxShape()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        var when = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        _commits.Setup(c => c.DescribeAsync(It.IsAny<string>(), "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo("abc1234abc1234abc1234abc1234abc1234abc12", "Rob", "Fix the thing", when));
        _commits.Setup(c => c.GetDiffAsync(It.IsAny<string>(), "abc1234abc1234abc1234abc1234abc1234abc12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([new BoardCommitDiffFile("a.cs", "csharp", "old", "new")], 1));

        var linked = await _service.LinkCommitAsync(_project, created.Key, "abc1234", Ct);
        Assert.Equal("abc1234", linked!.ShortSha);
        Assert.Equal("Fix the thing", linked.Message);
        Assert.Equal(when, linked.CommittedAt);

        // Viewing must use the saved snapshot even when git is no longer available.
        _commits.Reset();
        var diff = await _service.GetCommitDiffAsync(_project, created.Key, "abc1234", Ct);
        var file = Assert.Single(diff!.Files);
        Assert.Equal(("a.cs", "csharp", "old", "new"), (file.FileName, file.Language, file.OriginalContent, file.ModifiedContent));
        Assert.True(await _service.UnlinkCommitAsync(_project, created.Key, "abc1234", Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.GetCommitDiffAsync(_project, created.Key, "abc1234", Ct));
    }

    [Fact]
    public async Task UnlinkCommit_AmbiguousShortSha_DoesNotDeleteEitherLink()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        const string first = "abc1234aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "abc1234bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.NotNull(await _store.AddCommitAsync(_project, created.Id, first, "Rob", "One", DateTime.UtcNow, Snapshot(), Ct));
        Assert.NotNull(await _store.AddCommitAsync(_project, created.Id, second, "Rob", "Two", DateTime.UtcNow, Snapshot(), Ct));

        var error = await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.UnlinkCommitAsync(_project, created.Key, "abc1234", Ct));
        Assert.Contains("more than one linked commit", error.Message);
        Assert.Equal(2, (await _service.GetCommitsAsync(_project, created.Id, Ct))!.Count);

        var diffError = await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.GetCommitDiffAsync(_project, created.Key, "abc1234", Ct));
        Assert.Contains("more than one linked commit", diffError.Message);

        Assert.True(await _service.UnlinkCommitAsync(_project, created.Key, first, Ct));
        var remaining = Assert.Single((await _service.GetCommitsAsync(_project, created.Id, Ct))!);
        Assert.Equal(second, remaining.Sha);
    }

    [Fact]
    public async Task LinkCommit_CaptureFailure_DoesNotLink()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        const string sha = "abc1234abc1234abc1234abc1234abc1234abc12";
        var clone = Path.Combine(_root, "clone");
        _commits.Setup(c => c.DescribeAsync(clone, "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo(sha, "Rob", "Fix", DateTime.UtcNow));
        _commits.Setup(c => c.GetDiffAsync(clone, sha, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BoardValidationException("Could not capture the file."));

        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.LinkCommitAsync(_project, created.Key, "abc1234", Ct, gitWorkingDirectory: clone));

        Assert.Empty((await _service.GetCardAsync(_project, created.Id, Ct))!.Commits);
    }

    [Fact]
    public async Task GetDiff_LegacyLinkWithoutSnapshot_ExplainsHowToRecapture()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        // Model a link saved by the previous schema, which contained only metadata.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE BoardCommitSnapshots;
            INSERT INTO BoardCommits (CardId, Sha, Author, Message, CommittedUTC, LinkedUTC)
            VALUES ($card, 'abc1234abc1234abc1234abc1234abc1234abc12', 'Rob', 'Old link', $date, $date);
            """;
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
        _ = new BoardStore(_connectionString); // Opening an old database adds the snapshot table.

        var error = await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.GetCommitDiffAsync(_project, card.Id, "abc1234", Ct));
        Assert.Contains("Unlink it and link it again", error.Message);
    }

    [Fact]
    public async Task LiveSessions_ShowOnTheCardList_AndOnTheRail()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        const string sessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var linked = await _service.LinkSessionAsync(_project, created.Id, sessionId, "stored-tab", "base:claude", "claude", "Claude · VB-1", BoardSessionRecord.LaunchOrigin, Ct);
        Assert.False(linked!.Active);
        Assert.Equal(sessionId, linked.Id);

        _live.Sessions[sessionId] = "live-tab";
        var list = await _service.GetCardsAsync(_project, Ct);
        var summary = Assert.Single(list.Cards);
        Assert.Equal(sessionId, summary.ActiveSessionId);
        Assert.Equal("live-tab", summary.ActiveTabId);

        var detail = await _service.GetCardAsync(_project, created.Key, Ct);
        var session = Assert.Single(detail!.Sessions);
        Assert.True(session.Active);
        Assert.Equal("live-tab", session.TabId);

        var comment = await _service.AddCommentAsync(_project, created.Key, BoardAuthor.Agent("Claude · VB-1", "claude", sessionId), " done ", Ct);
        Assert.Equal("agent", comment!.Author.Kind);
        Assert.Equal("done", comment.Body);
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.AddCommentAsync(_project, created.Key, BoardAuthor.User(), "  ", Ct));
    }

    [Fact]
    public async Task LinkSession_RejectsNonGuidIds()
    {
        await _service.GetColumnsAsync(_project, Ct);
        var created = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);

        await Assert.ThrowsAsync<BoardValidationException>(() =>
            _service.LinkSessionAsync(_project, created.Id, "  ", null, "", "", "x", BoardSessionRecord.ManualOrigin, Ct));
        foreach (var payload in new[]
        {
            "<img src=x onerror=alert(1)>",
            "sess-9",
            Guid.Empty.ToString("D"),
            new string('a', 37),
            "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}"
        })
        {
            var error = await Assert.ThrowsAsync<BoardValidationException>(() =>
                _service.LinkSessionAsync(_project, created.Id, payload, null, "", "", "x", BoardSessionRecord.ManualOrigin, Ct));
            Assert.Contains("session id", error.Message);
        }

        var compact = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("N");
        var linked = await _service.LinkSessionAsync(_project, created.Id, compact, null, "", "", "Replay", BoardSessionRecord.ManualOrigin, Ct);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", linked!.Id);
        Assert.DoesNotContain((await _service.GetCardAsync(_project, created.Id, Ct))!.Sessions, s => s.Id.Contains('<', StringComparison.Ordinal));
    }

    private static SandboxDiffResponse Snapshot() =>
        new([new SandboxDiffFileResponse("a.cs", "csharp", "old\n", "new\n")], 1);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeLiveProbe : IBoardLiveSessionProbe
    {
        public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> GetLiveSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Sessions);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
