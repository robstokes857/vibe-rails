using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// The kanban MCP tools over a real temp store. The project resolver is faked so the tests own
/// which project (and which launching session) the tool believes it is running in.
/// </summary>
public sealed class BoardToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-tool-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _service;
    private readonly FakeResolver _resolver;
    private readonly BoardTool _tool;

    public BoardToolTests()
    {
        Directory.CreateDirectory(_root);
        _project = BoardStore.NormalizeProjectPath(Path.Combine(_root, "project"));
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString);
        var commits = new Mock<IBoardCommitService>(MockBehavior.Strict);
        commits.Setup(c => c.DescribeAsync(_project, "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo("abc1234abc1234abc1234abc1234abc1234abc12", "Rob", "Fix the race", DateTime.UtcNow));
        commits.Setup(c => c.DescribeAsync(_project, "nope", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BoardValidationException("That does not look like a commit sha."));
        commits.Setup(c => c.GetDiffAsync(_project, "abc1234abc1234abc1234abc1234abc1234abc12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([], 0));
        _service = new BoardService(_store, commits.Object, new NullBoardLiveSessionProbe());
        _resolver = new FakeResolver(_project);
        _tool = new BoardTool(_service, _resolver, _store);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListAndCreate_SeedLanes_AndReturnReadableText()
    {
        var lanes = await _tool.ListBoardColumns(Ct);
        Assert.Contains("- Backlog (id col_", lanes);
        Assert.Contains("- Ready (id col_", lanes);
        Assert.Contains("WIP limit 8", lanes);

        Assert.Equal("No cards match.", await _tool.ListBoardCards(cancellationToken: Ct));
        var created = await _tool.CreateBoardCard("Fix the race", "Two 401s overlap.", "build", "HIGH", "auth, bug", Ct);
        Assert.Equal("Created VB-1: Fix the race", created);

        var list = await _tool.ListBoardCards(cancellationToken: Ct);
        Assert.Equal("VB-1 [Build] (high) Fix the race", list);
        Assert.Equal("No cards match.", await _tool.ListBoardCards(column: "Review", cancellationToken: Ct));
        Assert.StartsWith("FAIL: lane not found: Nowhere", await _tool.ListBoardCards(column: "Nowhere", cancellationToken: Ct));
        Assert.StartsWith("FAIL: lane not found: Nowhere", await _tool.CreateBoardCard("x", column: "Nowhere", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Priority must be one of", await _tool.CreateBoardCard("x", priority: "urgent", cancellationToken: Ct));
    }

    [Fact]
    public async Task GetUpdateMoveCommentLink_WorkByKey()
    {
        await _tool.CreateBoardCard("Fix the race", "Two 401s overlap.", cancellationToken: Ct);

        var card = await _tool.GetBoardCard("vb-1", Ct);
        Assert.StartsWith("VB-1: Fix the race\nLane: Backlog · Priority: medium · Assignee: unassigned", card);
        Assert.Contains("Description:\nTwo 401s overlap.", card);
        Assert.Contains("Comments (0):\n(none)", card);

        Assert.Equal("Updated VB-1: Fix the race (critical, blocked)", await _tool.UpdateBoardCard("VB-1", priority: "critical", points: 5, blocked: true, cancellationToken: Ct));
        Assert.Equal("Updated VB-1: Fix the race (critical, blocked)", await _tool.UpdateBoardCard("VB-1", points: 0, cancellationToken: Ct));
        Assert.Null((await _store.FindCardAsync(_project, "VB-1", Ct))!.Points);

        Assert.Equal("Moved VB-1 to Review (position 0).", await _tool.MoveBoardCard("VB-1", "review", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Lane not found: Nowhere", await _tool.MoveBoardCard("VB-1", "Nowhere", cancellationToken: Ct));

        var comment = await _tool.AddBoardComment("Reproduced on two parallel saves.", "VB-1", Ct);
        Assert.StartsWith("Comment added to VB-1 as Agent at ", comment);
        Assert.StartsWith("FAIL: Comment cannot be empty.", await _tool.AddBoardComment("   ", "VB-1", Ct));

        Assert.Equal("Linked abc1234 \"Fix the race\" to VB-1.", await _tool.LinkBoardCommit("abc1234", "VB-1", Ct));
        Assert.StartsWith("FAIL: That does not look like a commit sha.", await _tool.LinkBoardCommit("nope", "VB-1", Ct));

        card = await _tool.GetBoardCard("VB-1", Ct);
        Assert.Contains("Lane: Review · Priority: critical", card);
        Assert.Contains("- [", card);
        Assert.Contains("] Agent: Reproduced on two parallel saves.", card);
        Assert.Contains("Linked commits (1):\n- abc1234 Fix the race (Rob)", card);

        Assert.StartsWith("FAIL: card not found on this project's board: VB-9", await _tool.GetBoardCard("VB-9", Ct));
    }

    [Fact]
    public async Task OmittedCard_DefaultsToTheLaunchingSession_AndAutoLinks()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        await _tool.CreateBoardCard("B", cancellationToken: Ct);

        // No session, no card: the tool says how to fix it rather than guessing.
        Assert.StartsWith("FAIL: no card given and this terminal is not linked to one.", await _tool.GetBoardCard(cancellationToken: Ct));

        // A VibeRails-launched session that was linked at launch resolves to its card…
        _resolver.CurrentSessionId = "sess-launch";
        await _store.LinkSessionAsync(_project, "VB-2", "sess-launch", "tab-1", "base:claude", "claude", "Claude · VB-2", BoardSessionRecord.LaunchOrigin, Ct);
        Assert.StartsWith("VB-2: B", await _tool.GetBoardCard(cancellationToken: Ct));
        var comment = await _tool.AddBoardComment("on it", cancellationToken: Ct);
        Assert.StartsWith("Comment added to VB-2 as Claude · VB-2", comment);

        // …and an unlinked VibeRails session that touches a card explicitly gets linked (origin mcp).
        _resolver.CurrentSessionId = "sess-adhoc";
        Assert.StartsWith("Comment added to VB-1 as Agent", await _tool.AddBoardComment("picking this up", "VB-1", Ct));
        var link = await _store.FindSessionLinkAsync("sess-adhoc", Ct);
        Assert.NotNull(link);
        Assert.Equal((await _store.FindCardAsync(_project, "VB-1", Ct))!.Id, link!.CardId);
        var detail = await _store.GetCardDetailAsync(_project, "VB-1", Ct);
        Assert.Equal(BoardSessionRecord.McpOrigin, Assert.Single(detail!.Sessions).Origin);
        // From then on the omitted-card default follows that link.
        Assert.StartsWith("VB-1: A", await _tool.GetBoardCard(cancellationToken: Ct));
    }

    [Theory]
    [InlineData("codex", null, "Codex")]
    [InlineData("claude", "Review worker", "Review worker")]
    public async Task FirstComment_UsesTheSavedSessionAgent_BeforeAutoLink(string cli, string? environment, string expectedLabel)
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-unlinked";
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Cli TEXT, EnvironmentName TEXT);
                INSERT INTO Sessions VALUES ($id, $cli, $environment);
                """;
            insert.Parameters.AddWithValue("$id", _resolver.CurrentSessionId);
            insert.Parameters.AddWithValue("$cli", cli);
            insert.Parameters.AddWithValue("$environment", (object?)environment ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var result = await _tool.AddBoardComment("Starting", "VB-1", Ct);
        Assert.StartsWith($"Comment added to VB-1 as {expectedLabel} at ", result);
        var detail = (await _store.GetCardDetailAsync(_project, "VB-1", Ct))!;
        var author = Assert.Single(detail.Comments).Author;
        Assert.Equal(expectedLabel, author.Label);
        Assert.Equal(cli, author.Cli, ignoreCase: true);
        Assert.Equal(_resolver.CurrentSessionId, author.SessionId);
        Assert.Equal(expectedLabel, Assert.Single(detail.Sessions).DisplayName);
    }

    [Fact]
    public async Task OldGenericComments_ResolveFromTheirSession_WithoutChangingUserComments()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        await _store.AddCommentAsync(_project, "VB-1", BoardAuthor.Agent("Agent", null, "sess-old"), "Old agent note", Ct);
        await _store.AddCommentAsync(_project, "VB-1", BoardAuthor.User(), "User note", Ct);
        await _store.LinkSessionAsync(_project, "VB-1", "sess-old", null, "base:codex", "codex", "Codex", BoardSessionRecord.McpOrigin, Ct);

        var result = await _tool.GetBoardCard("VB-1", Ct);
        Assert.Contains("] Codex: Old agent note", result);
        Assert.Contains("] You: User note", result);
        var stored = (await _store.GetCardDetailAsync(_project, "VB-1", Ct))!;
        Assert.Equal("Agent", stored.Comments.Single(comment => comment.Body == "Old agent note").Author.Label);
    }

    private sealed class FakeResolver(string project) : IBoardProjectResolver
    {
        public string GitWorkingDirectory => project;
        public string? CurrentSessionId { get; set; }
        public Task<string> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(project);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
