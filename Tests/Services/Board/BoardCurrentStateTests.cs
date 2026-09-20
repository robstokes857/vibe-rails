using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCurrentStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-current-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly FakeLiveProbe _live = new();
    private readonly Mock<ITerminalTabHostService> _tabs = new(MockBehavior.Strict);
    private readonly BoardService _service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCurrentStateTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString);
        _service = new BoardService(_store, Mock.Of<IBoardCommitService>(), _live);
    }

    [Fact]
    public async Task LaunchUsesTheReadCardSnapshot_WithoutRewritingLaterEdits()
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
        Assert.Equal("edited during startup", (await _store.FindCardAsync(_project, card.Id, Ct))!.Description);
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
    }

    [Fact]
    public async Task ReplacementsKeepOnlyTheLatestDescription()
    {
        var card = await CreateAsync();
        await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Description: "second"), Ct);
        await _service.UpdateCardAsync(_project, card.Id, new UpdateBoardCardRequest(Description: "last write"), Ct);
        Assert.Equal("last write", (await new BoardStore(_connectionString).FindCardAsync(_project, card.Id, Ct))!.Description);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'BoardDescription%';";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task OversizedAppendDoesNotPartiallyUpdateTheCard()
    {
        var card = await CreateAsync();
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.UpdateCardAsync(_project, card.Id,
            new UpdateBoardCardRequest(Title: "Must not land", DescriptionAppend: new string('x', BoardService.MaxDescriptionLength)), Ct));
        var unchanged = (await _store.FindCardAsync(_project, card.Id, Ct))!;
        Assert.Equal(card.Title, unchanged.Title);
        Assert.Equal(card.Description, unchanged.Description);
    }

    private Task<BoardCardResponse> CreateAsync() => _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A", Description: "first\nline"), Ct);

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
