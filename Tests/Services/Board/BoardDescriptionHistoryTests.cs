using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardDescriptionHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-history-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly FakeLiveProbe _live = new();
    private readonly Mock<ITerminalTabHostService> _tabs = new(MockBehavior.Strict);
    private readonly BoardService _service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardDescriptionHistoryTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString);
        _service = new BoardService(_store, Mock.Of<IBoardCommitService>(), _live);
    }

    [Fact]
    public async Task DescriptionRevisions_AreImmutable_AndNoOpsDoNotAppend()
    {
        var card = await CreateAsync();
        Assert.Equal(1, card.DescriptionRevision);
        var unchanged = await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Description: "  first\r\nline  ", ExpectedDescriptionRevision: 1), Ct);
        Assert.False(unchanged!.DescriptionChanged);
        Assert.Equal(1, unchanged.DescriptionRevision);
        var updated = await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Description: "second", ExpectedDescriptionRevision: 1), Ct);
        Assert.True(updated!.DescriptionChanged);
        Assert.Equal(2, updated.DescriptionRevision);
        await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Title: "Renamed", Priority: "high"), Ct);

        var history = (await _store.GetDescriptionHistoryAsync(_project, card.Id, Ct))!;
        Assert.Equal([2, 1], history.Revisions.Select(r => r.Revision));
        Assert.Equal(["second", "first\nline"], history.Revisions.Select(r => r.Description));
        Assert.Equal("user", history.Revisions[0].Source);
        Assert.Null(await _store.GetDescriptionHistoryAsync(Path.Combine(_root, "other-project"), card.Id, Ct));
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleEditor_CannotOverwriteNewRevision_FromAnotherStore()
    {
        var card = await CreateAsync();
        var other = new BoardStore(_connectionString);
        var stale = (await other.FindCardAsync(_project, card.Id, Ct))!;
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Description: "new scope", ExpectedDescriptionRevision: 1), Ct);
        await Assert.ThrowsAsync<BoardConflictException>(() => other.UpdateCardAsync(_project, card.Id,
            new BoardCardPatch(Description: "stale overwrite", ExpectedDescriptionRevision: stale.DescriptionRevision), Ct));
        Assert.Equal("new scope", (await other.FindCardAsync(_project, card.Id, Ct))!.Description);
        Assert.Equal(2, (await other.GetDescriptionHistoryAsync(_project, card.Id, Ct))!.Revisions.Count);
    }

    [Fact]
    public async Task FailedRevisionInsert_RollsBackDescriptionUpdate()
    {
        var card = await CreateAsync();
        await ExecuteAsync("CREATE TRIGGER RejectDescription BEFORE INSERT ON BoardDescriptionRevisions BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Description: "must roll back"), Ct));
        var unchanged = (await _store.FindCardAsync(_project, card.Id, Ct))!;
        Assert.Equal("first\nline", unchanged.Description);
        Assert.Equal(1, unchanged.DescriptionRevision);
    }

    [Fact]
    public async Task LegacyBaseline_DoesNotInventDescriptionForEarlierSessions()
    {
        var card = await CreateAsync();
        await LinkAsync(card.Id, Guid.NewGuid().ToString(), "old-tab");
        await ExecuteAsync("DROP TABLE BoardDescriptionRevisionAttachments; DROP TABLE BoardDescriptionSessionEvents; DROP TABLE BoardDescriptionRevisions;");
        var before = DateTime.UtcNow;
        var migrated = new BoardStore(_connectionString);
        var history = (await migrated.GetDescriptionHistoryAsync(_project, card.Id, Ct))!;
        var baseline = Assert.Single(history.Revisions);
        Assert.Equal("imported", baseline.Source);
        Assert.True(baseline.CreatedAt >= before);
        Assert.Empty(baseline.Sessions);
        Assert.Single((await new BoardStore(_connectionString).GetDescriptionHistoryAsync(_project, card.Id, Ct))!.Revisions);
    }

    [Fact]
    public async Task SavingDescription_RecordsLiveSessionContext_AndNeverSendsInput()
    {
        var card = await CreateAsync();
        var running = Guid.NewGuid().ToString();
        var ended = Guid.NewGuid().ToString();
        var unrelated = Guid.NewGuid().ToString();
        await LinkAsync(card.Id, running, "tab-1");
        await LinkAsync(card.Id, ended, "tab-2");
        _live.Sessions[running] = "tab-1";
        _live.Sessions[unrelated] = "other-tab";
        var updated = await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Description: "new scope"), Ct);
        var history = (await _service.GetDescriptionHistoryAsync(_project, card.Id, Ct))!;
        var entry = Assert.Single(history.Revisions[0].Sessions);
        Assert.Equal(running, entry.SessionId);
        Assert.Equal("updated", entry.Kind);
        Assert.Equal("recorded", entry.Status);
        Assert.True(updated!.DescriptionChanged);
        _tabs.VerifyNoOtherCalls();
        await _store.UnlinkSessionAsync(_project, card.Id, running, Ct);
        Assert.Single((await _service.GetDescriptionHistoryAsync(_project, card.Id, Ct))!.Revisions[0].Sessions);
    }

    [Fact]
    public async Task ReadEvent_IsRecordedWithoutALink_AndNamesTheReadersOwnCard()
    {
        var mine = await CreateAsync();
        var other = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Other"), Ct);
        var session = Guid.NewGuid().ToString();
        await LinkAsync(mine.Id, session, "tab-1");

        // Reading a card this session does not own is recorded, and leaves the link where it was.
        Assert.True(await _store.RecordDescriptionSessionAsync(_project, other.Id, 1, session, "read", Ct));
        var read = Assert.Single((await _store.GetDescriptionHistoryAsync(_project, other.Id, Ct))!.Revisions[0].Sessions);
        Assert.Equal("read", read.Kind);
        Assert.Equal($"working {mine.Key}", read.Message);
        Assert.Equal(mine.Id, (await _store.FindSessionLinkAsync(session, Ct))!.CardId);
        Assert.Empty((await _store.GetCardDetailAsync(_project, other.Id, Ct))!.Sessions);

        // Re-reading the same revision writes nothing: the common path never takes the write lock.
        Assert.False(await _store.RecordDescriptionSessionAsync(_project, other.Id, 1, session, "read", Ct));

        // A read of its own card carries no note; launch/updated still need the link.
        Assert.True(await _store.RecordDescriptionSessionAsync(_project, mine.Id, 1, session, "read", Ct));
        Assert.Null((await _store.GetDescriptionHistoryAsync(_project, mine.Id, Ct))!.Revisions[0].Sessions.Single(s => s.Kind == "read").Message);
        Assert.False(await _store.RecordDescriptionSessionAsync(_project, other.Id, 1, Guid.NewGuid().ToString(), "updated", Ct));
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchHistory_UsesExactPromptRevision_WhenCardChangesDuringLaunch()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Ship", Description: "original", Assignee: "base:codex",
            BaseLlmOptions: new BaseLlmOptions("gpt-6", "high", "plan")), Ct);
        var session = Guid.NewGuid().ToString();
        _tabs.SetupGet(t => t.MaxTabs).Returns(8);
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TerminalTabStatusResponse("tab-1", DateTime.UtcNow, false));
        StartTerminalRequest? captured = null;
        _tabs.Setup(t => t.StartSessionAsync("tab-1", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, StartTerminalRequest request, CancellationToken token) =>
            {
                captured = request;
                await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Description: "edited during startup"), token);
                return new TerminalStatusResponse(true, session, "codex", _project);
            });
        var launcher = new BoardLaunchService(_store, Mock.Of<IRepository>(), _tabs.Object);
        await launcher.LaunchAsync(_project, card.Id, null, Ct);
        Assert.Contains("original", captured!.InitialPrompt);
        Assert.Equal("VB-1 · Ship", captured.Title);
        // Codex has no startup mode, so the stored "plan" is dropped rather than reaching the CLI.
        Assert.Equal("", captured.BaseLlmOptions!.Mode);
        Assert.True(captured.AuthorizeBoardTools);
        var history = (await _store.GetDescriptionHistoryAsync(_project, card.Id, Ct))!;
        Assert.Equal(2, history.CurrentRevision);
        Assert.Empty(history.Revisions[0].Sessions);
        Assert.Equal("launch", Assert.Single(history.Revisions[1].Sessions).Kind);
        Assert.Equal("VB-1 · Ship", Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Sessions).DisplayName);
    }

    [Fact]
    public async Task BaseOptions_RoundTrip_AndClearWhenAssigneeChanges()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Model", Assignee: "base:codex",
            BaseLlmOptions: new BaseLlmOptions("gpt-5.5", "max", "plan")), Ct);
        Assert.Equal("xhigh", card.BaseLlmOptions!.Effort);
        Assert.Equal("", (await new BoardStore(_connectionString).FindCardAsync(_project, card.Id, Ct))!.BaseLlmOptions!.Mode);
        var updated = await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Assignee: "env:7:codex"), Ct);
        Assert.Null(updated!.BaseLlmOptions);
        Assert.Equal(1, updated.DescriptionRevision);
    }

    [Fact]
    public async Task AttachmentManifest_RemainsReadableAfterRemoval()
    {
        var card = await CreateAsync();
        var attachment = (await _service.AddAttachmentAsync(_project, card.Id,
            new AddBoardAttachmentRequest(Name: "CLI_OPTIONS.md", DataUrl: "data:text/markdown;base64,IyBDTEkgb3B0aW9ucw=="), Ct))!;
        var withAttachment = (await _store.FindCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(2, withAttachment.DescriptionRevision);
        await _service.DeleteAttachmentAsync(_project, card.Id, attachment.Id, Ct);
        var history = (await _service.GetDescriptionHistoryAsync(_project, card.Id, Ct))!;
        Assert.Equal([3, 2, 1], history.Revisions.Select(r => r.Revision));
        Assert.Empty(history.Revisions[0].Attachments);
        Assert.Equal(attachment.Id, Assert.Single(history.Revisions[1].Attachments).Id);
        Assert.Empty(history.Revisions[2].Attachments);
        Assert.Equal("# CLI options", BoardService.ReadAttachmentText((await _service.GetAttachmentContentAsync(_project, card.Id, attachment.Id, Ct))!));
        // History names its files but never repeats their bytes — a revision-by-attachment cross
        // product of data URLs is what made opening this rail ship megabytes.
        Assert.All(history.Revisions.SelectMany(r => r.Attachments), file => Assert.Empty(file.Url));
        _ = new BoardStore(_connectionString);
        Assert.Equal(attachment.Id, Assert.Single((await _service.GetDescriptionHistoryAsync(_project, card.Id, Ct))!.Revisions[1].Attachments).Id);
    }

    private Task<BoardCardResponse> CreateAsync() => _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A", Description: "first\nline"), Ct);

    private Task<BoardSessionRecord?> LinkAsync(string cardId, string session, string tab) =>
        _store.LinkSessionAsync(_project, cardId, session, tab, "base:codex", "codex", "VB-1 · A", BoardSessionRecord.LaunchOrigin, Ct);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
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
