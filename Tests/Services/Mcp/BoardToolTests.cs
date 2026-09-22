using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DTOs;
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
    private readonly string _stateConnectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _service;
    private readonly FakeResolver _resolver;
    private readonly BoardTool _tool;

    public BoardToolTests()
    {
        Directory.CreateDirectory(_root);
        _project = BoardStore.NormalizeProjectPath(Path.Combine(_root, "project"));
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _stateConnectionString = $"Data Source={Path.Combine(_root, "state.db")};Pooling=False";
        _store = new BoardStore(_connectionString, _stateConnectionString);
        var commits = new Mock<IBoardCommitService>(MockBehavior.Strict);
        commits.Setup(c => c.DescribeAsync(_project, "abc1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo("abc1234abc1234abc1234abc1234abc1234abc12", "Rob", "Fix the race", DateTime.UtcNow));
        commits.Setup(c => c.DescribeAsync(_project, "nope", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BoardValidationException("That does not look like a commit sha."));
        commits.Setup(c => c.GetDiffAsync(_project, "abc1234abc1234abc1234abc1234abc1234abc12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([], 0));
        // An old commit (2020) that a session links today: "since" must treat the link as activity.
        commits.Setup(c => c.DescribeAsync(_project, "01d1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitInfo("01d123401d123401d123401d123401d123401d12", "Rob", "Ancient fix", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        commits.Setup(c => c.GetDiffAsync(_project, "01d123401d123401d123401d123401d123401d12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCommitDiff([], 0));
        _service = new BoardService(_store, commits.Object, new NullBoardLiveSessionProbe());
        _resolver = new FakeResolver(_project);
        _tool = new BoardTool(_service, _resolver, _store);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListAndCreate_SeedLanes_AndReturnReadableText()
    {
        var lanes = await _tool.ListBoardColumns(cancellationToken: Ct);
        Assert.Contains("- Backlog (id col_", lanes);
        Assert.Contains("- Ready (id col_", lanes);
        Assert.DoesNotContain("WIP", lanes);

        Assert.Equal("No cards match.", await _tool.ListBoardCards(cancellationToken: Ct));
        var created = await _tool.CreateBoardCard("Fix the race", "Two 401s overlap.", "build", "HIGH", "auth, bug", "bug", cancellationToken: Ct);
        Assert.Equal("Created VB-1: Fix the race", created);

        var list = await _tool.ListBoardCards(cancellationToken: Ct);
        Assert.Equal("VB-1 [Build] [Bug] (high) Fix the race", list);
        Assert.Equal(list, await _tool.ListBoardCards(type: "bug", cancellationToken: Ct));
        Assert.Equal("No cards match.", await _tool.ListBoardCards(type: "feature", cancellationToken: Ct));
        Assert.Equal("No cards match.", await _tool.ListBoardCards(column: "Review", cancellationToken: Ct));
        Assert.StartsWith("FAIL: lane not found: Nowhere", await _tool.ListBoardCards(column: "Nowhere", cancellationToken: Ct));
        Assert.StartsWith("FAIL: lane not found: Nowhere", await _tool.CreateBoardCard("x", column: "Nowhere", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Priority must be one of", await _tool.CreateBoardCard("x", priority: "urgent", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Type must be one of", await _tool.CreateBoardCard("x", type: "incident", cancellationToken: Ct));
    }

    [Fact]
    public async Task GetUpdateMoveCommentLink_WorkByKey()
    {
        await _tool.CreateBoardCard("Fix the race", "Two 401s overlap.", cancellationToken: Ct);

        var card = await _tool.GetBoardCard("vb-1", cancellationToken: Ct);
        Assert.StartsWith("VB-1: Fix the race\nLane: Backlog · Type: Task · Priority: medium · Assignee: unassigned", card);
        Assert.Contains("Description:\nTwo 401s overlap.", card);
        Assert.Contains("Comments (0):\n(none)", card);

        Assert.Equal("Updated VB-1: Fix the race (critical, blocked)", await _tool.UpdateBoardCard("VB-1", priority: "critical", points: 5, blocked: true, cancellationToken: Ct));
        Assert.Equal("Updated VB-1: Fix the race (critical, blocked)", await _tool.UpdateBoardCard("VB-1", points: 0, cancellationToken: Ct));
        Assert.Null((await _store.FindCardAsync(_project, "VB-1", Ct))!.Points);

        Assert.Equal("Moved VB-1 to Review (position 0).", await _tool.MoveBoardCard("VB-1", "review", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Lane not found: Nowhere", await _tool.MoveBoardCard("VB-1", "Nowhere", cancellationToken: Ct));

        var comment = await _tool.AddBoardComment("Reproduced on two parallel saves.", "VB-1", Ct);
        Assert.Matches(@"^Comment cm_[0-9a-f]{12} added to VB-1 as Agent at ", comment);
        Assert.StartsWith("FAIL: Comment cannot be empty.", await _tool.AddBoardComment("   ", "VB-1", Ct));

        Assert.Equal("Linked abc1234 \"Fix the race\" to VB-1.", await _tool.LinkBoardCommit("abc1234", "VB-1", Ct));
        Assert.StartsWith("FAIL: That does not look like a commit sha.", await _tool.LinkBoardCommit("nope", "VB-1", Ct));

        card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains("Lane: Review · Type: Task · Priority: critical", card);
        Assert.Contains("- [", card);
        Assert.Matches(@"\] Agent \(cm_[0-9a-f]{12}\): Reproduced on two parallel saves\.", card);
        Assert.Contains("Linked commits (1):\n- abc1234 Fix the race (Rob)", card);

        Assert.StartsWith("FAIL: card not found on this project's board: VB-9", await _tool.GetBoardCard("VB-9", cancellationToken: Ct));
    }

    [Fact]
    public async Task GetBoardCard_IncludesLinkedCardsFromOtherBoards_EvenWithSinceFilter()
    {
        var first = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "First"), Ct);
        var sprint = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Sprint 2"), Ct);
        var second = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Related work", BoardId: sprint.Id), Ct);
        await _service.LinkCardAsync(_project, first.Id, second.Id, Ct);
        var text = await _tool.GetBoardCard(first.Key, since: DateTime.UtcNow.AddMinutes(1).ToString("O"), cancellationToken: Ct);
        Assert.Contains("Linked cards (1):\n- VB-2: Related work (Sprint 2 · Backlog)", text);
        Assert.Contains("Read a linked card by passing its key to get_board_card.", text);
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
        Assert.Matches(@"^Comment cm_[0-9a-f]{12} added to VB-2 as Claude · VB-2", comment);

        // …and an unlinked VibeRails session that touches a card explicitly gets linked (origin mcp).
        _resolver.CurrentSessionId = "sess-adhoc";
        Assert.Matches(@"^Comment cm_[0-9a-f]{12} added to VB-1 as Agent", await _tool.AddBoardComment("picking this up", "VB-1", Ct));
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
        await using (var connection = new SqliteConnection(_stateConnectionString))
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
        Assert.Matches($@"^Comment cm_[0-9a-f]{{12}} added to VB-1 as {System.Text.RegularExpressions.Regex.Escape(expectedLabel)} at ", result);
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

        var result = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Matches(@"\] Codex \(cm_[0-9a-f]{12}\): Old agent note", result);
        Assert.Matches(@"\] You \(cm_[0-9a-f]{12}\): User note", result);
        var stored = (await _store.GetCardDetailAsync(_project, "VB-1", Ct))!;
        Assert.Equal("Agent", stored.Comments.Single(comment => comment.Body == "Old agent note").Author.Label);
    }

    [Fact]
    public async Task ReadingDoesNotClaimACard_EditingLinksTheSession()
    {
        await _tool.CreateBoardCard("A", "first scope", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-reader";
        Assert.Contains("Description:\nfirst scope", await _tool.GetBoardCard("VB-1", cancellationToken: Ct));
        Assert.Null(await _store.FindSessionLinkAsync("sess-reader", Ct));
        await _tool.UpdateBoardCard("VB-1", description: "second scope", cancellationToken: Ct);
        Assert.NotNull(await _store.FindSessionLinkAsync("sess-reader", Ct));
    }

    [Fact]
    public async Task ReadingAnotherCard_DoesNotClaimIt()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        await _tool.CreateBoardCard("B", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-working-vb1";
        await _store.LinkSessionAsync(_project, "VB-1", "sess-working-vb1", "tab-1", "base:claude", "claude", "Claude · VB-1", BoardSessionRecord.LaunchOrigin, Ct);

        Assert.StartsWith("VB-2: B", await _tool.GetBoardCard("VB-2", cancellationToken: Ct));

        Assert.Empty((await _store.GetCardDetailAsync(_project, "VB-2", Ct))!.Sessions);
        Assert.Equal((await _store.FindCardAsync(_project, "VB-1", Ct))!.Id,
            (await _store.FindSessionLinkAsync("sess-working-vb1", Ct))!.CardId);
    }

    [Fact]
    public async Task AttachCurrentSession_IsExplicitIdempotent_AndKeepsTheOriginalDefault()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        await _tool.CreateBoardCard("B", cancellationToken: Ct);
        Assert.StartsWith("FAIL: this terminal has no VibeRails session", await _tool.AttachBoardSession("VB-2", Ct));
        _resolver.CurrentSessionId = "11111111-1111-4111-8111-111111111111";
        await _store.LinkSessionAsync(_project, "VB-1", _resolver.CurrentSessionId, "tab-1", "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);

        // Browsing, comments, notes and commit references alone do not attach a second card.
        await _tool.GetBoardCard("VB-2", cancellationToken: Ct);
        await _tool.AddBoardComment("Related work", "VB-2", Ct);
        await _tool.AppendBoardNote("Investigation", "VB-2", Ct);
        await _tool.LinkBoardCommit("abc1234", "VB-2", Ct);
        Assert.Empty((await _store.GetCardDetailAsync(_project, "VB-2", Ct))!.Sessions);

        Assert.StartsWith("FAIL: pass the card key", await _tool.AttachBoardSession(" ", Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.AttachBoardSession("VB-999", Ct));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => _tool.AttachBoardSession("VB-2", Ct), Ct)));
        Assert.All(attempts, result => Assert.StartsWith("Attached session 11111111-1111-4111-8111-111111111111 to VB-2", result));
        var attached = Assert.Single((await _store.GetCardDetailAsync(_project, "VB-2", Ct))!.Sessions);
        Assert.Equal("tab-1", attached.TabId);
        Assert.Equal("base:codex", attached.Selection);
        Assert.StartsWith("VB-1: A", await _tool.GetBoardCard(cancellationToken: Ct));

        // A repeat from the original card is safe even when the commit is already linked.
        await _tool.LinkBoardCommit("abc1234", cancellationToken: Ct);
        foreach (var key in new[] { "VB-1", "VB-2" })
        {
            Assert.Single((await _service.GetCardAsync(_project, key, Ct))!.Commits);
            Assert.NotNull(await _service.GetCommitDiffAsync(_project, key, "abc1234", Ct));
        }
    }

    [Fact]
    public async Task LinkCommit_OneCallSharesWithEveryAttachedCard_AndCanBeRepeatedFromAnyCard()
    {
        var first = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "A"), Ct);
        var board = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Other board"), Ct);
        var second = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "B", BoardId: board.Id), Ct);
        var third = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "C"), Ct);
        var unrelated = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Unrelated"), Ct);
        _resolver.CurrentSessionId = "11111111-1111-4111-8111-111111111111";
        foreach (var card in new[] { first, second, third })
            Assert.StartsWith("Attached session", await _tool.AttachBoardSession(card.Key, Ct));

        var result = await _tool.LinkBoardCommit("abc1234", second.Key, Ct);
        Assert.Contains("Also linked to every card attached to this session", result);
        foreach (var card in new[] { first, second, third })
        {
            var detail = (await _service.GetCardAsync(_project, card.Id, Ct))!;
            Assert.Equal("abc1234", Assert.Single(detail.Commits).ShortSha);
            Assert.NotNull(await _service.GetCommitDiffAsync(_project, card.Id, "abc1234", Ct));
        }
        Assert.Empty((await _service.GetCardAsync(_project, unrelated.Id, Ct))!.Commits);
        var before = Assert.Single(await _store.GetCommitsAsync(_project, first.Id, Ct));
        foreach (var key in new[] { first.Key, second.Key, third.Key })
            Assert.StartsWith("Linked abc1234", await _tool.LinkBoardCommit("abc1234", key, Ct));
        Assert.Equal(before, Assert.Single(await _store.GetCommitsAsync(_project, first.Id, Ct)));

        // An explicit target need not be attached, but the session's cards still get the commit.
        Assert.StartsWith("Linked 01d1234", await _tool.LinkBoardCommit("01d1234", unrelated.Key, Ct));
        foreach (var card in new[] { first, second, third, unrelated })
            Assert.Contains((await _service.GetCardAsync(_project, card.Id, Ct))!.Commits, c => c.ShortSha == "01d1234");
        Assert.Empty((await _service.GetCardAsync(_project, unrelated.Id, Ct))!.Sessions);
    }

    [Fact]
    public async Task AttachCurrentSession_CannotCrossProjects_AndCanEstablishTheFirstLink()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        var other = Path.Combine(_root, "other");
        var foreign = await _service.CreateCardAsync(other, new CreateBoardCardRequest(Title: "Foreign"), Ct);
        _resolver.CurrentSessionId = "22222222-2222-4222-8222-222222222222";
        await _store.LinkSessionAsync(other, foreign.Id, "22222222-2222-4222-8222-222222222222", null, "", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        Assert.StartsWith("FAIL: That session is linked to a card in another project", await _tool.AttachBoardSession("VB-1", Ct));
        Assert.StartsWith("FAIL: card not found", await _tool.AttachBoardSession(foreign.Id, Ct));
        Assert.Empty((await _store.GetCardDetailAsync(_project, "VB-1", Ct))!.Sessions);

        _resolver.CurrentSessionId = "33333333-3333-4333-8333-333333333333";
        Assert.StartsWith("Attached session 33333333-3333-4333-8333-333333333333 to VB-1", await _tool.AttachBoardSession("VB-1", Ct));
        Assert.StartsWith("VB-1: A", await _tool.GetBoardCard(cancellationToken: Ct));
    }

    [Fact]
    public async Task AttentionFlag_RoundTripsThroughMcp_WithoutChangingBlockedOrDescription()
    {
        await _tool.CreateBoardCard("Review decision", "Keep this text", cancellationToken: Ct);
        Assert.Contains("needs your attention", await _tool.UpdateBoardCard("VB-1", flagged: true, cancellationToken: Ct));
        Assert.Contains("FLAGGED: needs your attention", await _tool.GetBoardCard("VB-1", cancellationToken: Ct));
        Assert.Contains("FLAGGED: needs your attention", await _tool.ListBoardCards(cancellationToken: Ct));
        var saved = (await new BoardStore(_connectionString, _stateConnectionString).FindCardAsync(_project, "VB-1", Ct))!;
        Assert.True(saved.Flagged);
        Assert.False(saved.Blocked);
        Assert.Equal("Keep this text", saved.Description);
        await _tool.UpdateBoardCard("VB-1", priority: "high", cancellationToken: Ct);
        Assert.True((await _store.FindCardAsync(_project, "VB-1", Ct))!.Flagged);
        await _tool.UpdateBoardCard("VB-1", flagged: false, cancellationToken: Ct);
        Assert.DoesNotContain("FLAGGED", await _tool.GetBoardCard("VB-1", cancellationToken: Ct));
    }

    // ------------------------------------------------------------------ agent-workflow additions (2026-09-17)

    [Fact]
    public async Task GetBoardCard_ListsTheLanes_AndTheAgentNotesTail()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        var card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains("\nLanes: Backlog → Ready → Build → Review → Done\n", card);
        Assert.Contains("Agent notes (0):\n(none — use append_board_note to checkpoint findings as you work)", card);
    }

    [Fact]
    public async Task Notes_AreAScratchpad_OutsideTheCommentStream()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-notes";

        var added = await _tool.AppendBoardNote("checkpoint: found 3 candidates", "VB-1", Ct);
        Assert.Matches(@"^Note note_[0-9a-f]{12} added to VB-1 as Agent at ", added);
        Assert.StartsWith("FAIL: Note cannot be empty.", await _tool.AppendBoardNote("  ", "VB-1", Ct));
        await _tool.AddBoardComment("visible progress", "VB-1", Ct);

        // The comment stream and its count ignore notes; the notes section shows them.
        var card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains("Comments (1):\n", card);
        Assert.DoesNotContain("Comments (2)", card);
        Assert.Contains("Agent notes (1):\n", card);
        Assert.Matches(@"\] Agent \(note_[0-9a-f]{12}\): checkpoint: found 3 candidates", card);
        Assert.Equal(1, (await _store.FindCardAsync(_project, "VB-1", Ct))!.CommentCount);
        Assert.Equal("VB-1 [Backlog] [Task] (medium) A — 1 comment", await _tool.ListBoardCards(cancellationToken: Ct));

        var notes = await _tool.GetBoardNotes("VB-1", cancellationToken: Ct);
        Assert.StartsWith("Agent notes on VB-1 (1):\n", notes);
        Assert.Contains("checkpoint: found 3 candidates", notes);
        Assert.DoesNotContain("visible progress", notes);

        // Writing a note links the session like any other write.
        Assert.NotNull(await _store.FindSessionLinkAsync("sess-notes", Ct));
    }

    [Fact]
    public async Task GetBoardCard_ShowsOnlyTheNotesTail_AndPointsAtGetBoardNotes()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        for (var i = 0; i < 6; i++)
            await _tool.AppendBoardNote($"note {i} " + new string((char)('a' + i), 900), "VB-1", Ct);

        var card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains("Agent notes (6):\n(earlier notes omitted; read them all with get_board_notes)", card);
        Assert.DoesNotContain("note 0 ", card);
        Assert.Contains("note 5 ", card);
        var all = await _tool.GetBoardNotes("VB-1", cancellationToken: Ct);
        Assert.Contains("note 0 ", all);
        Assert.Contains("note 5 ", all);
    }

    [Fact]
    public async Task Since_FiltersActivity_AndCountsWhatItHides()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        await _tool.AddBoardComment("old", "VB-1", Ct);
        await _tool.AppendBoardNote("old note", "VB-1", Ct);
        await _tool.LinkBoardCommit("abc1234", "VB-1", Ct);
        var cutoff = DateTime.UtcNow.AddMilliseconds(5);
        await Task.Delay(20, Ct);
        await _tool.AddBoardComment("new", "VB-1", Ct);
        // Committed in 2020, linked now: it is this session's activity and must show.
        await _tool.LinkBoardCommit("01d1234", "VB-1", Ct);

        var card = await _tool.GetBoardCard("VB-1", since: cutoff.ToString("O"), cancellationToken: Ct);
        Assert.Contains("Showing activity since ", card);
        Assert.Contains("Comments (1, 1 earlier hidden):\n", card);
        Assert.DoesNotContain("): old\n", card);
        Assert.Contains("): new", card);
        Assert.Contains("Linked commits (1, 1 earlier hidden):\n- 01d1234 Ancient fix (Rob)\n", card);
        Assert.DoesNotContain("abc1234 Fix the race", card);
        Assert.Contains("Agent notes (0, 1 earlier hidden):\n", card);

        Assert.StartsWith("FAIL: since must be an ISO-8601 timestamp", await _tool.GetBoardCard("VB-1", since: "yesterday", cancellationToken: Ct));
        Assert.Contains("(0 of 1 since ", await _tool.GetBoardNotes("VB-1", since: cutoff.ToString("O"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Sessions_ShowTheirId_Outcome_AndLastComment()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        var sessionId = "9a3e5d6a-d28d-421a-9979-e5be4232e916";
        await using (var connection = new SqliteConnection(_stateConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Cli TEXT, EnvironmentName TEXT, EndedUTC TEXT, ExitCode INTEGER);
                INSERT INTO Sessions VALUES ($id, 'codex', NULL, '2026-09-17T12:49:28.1102516Z', 137);
                CREATE TABLE ChatSummary (Id INTEGER PRIMARY KEY, SessionId TEXT NOT NULL UNIQUE, SummaryText TEXT NOT NULL DEFAULT '', Date TEXT NOT NULL);
                INSERT INTO ChatSummary (SessionId, SummaryText, Date) VALUES ($id, 'Audited the card and moved it to Review.', '2026-09-17');
                """;
            insert.Parameters.AddWithValue("$id", sessionId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        _resolver.CurrentSessionId = sessionId;
        await _tool.AddBoardComment("first", "VB-1", Ct);
        await _tool.AddBoardComment("Revision 2 audit completed.", "VB-1", Ct);

        var card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains($" · ended 2026-09-17 12:49:28Z (exit 137) · session {sessionId}\n", card);
        Assert.Contains("    last comment [", card);
        Assert.Contains("]: Revision 2 audit completed.\n", card);
        Assert.Contains("    summary: Audited the card and moved it to Review.\n", card);
    }

    [Fact]
    public async Task DescriptionAppend_PreservesCurrentText_AndValidatesAllFields()
    {
        await _tool.CreateBoardCard("A", "first scope", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-append";

        Assert.Equal("Appended to the description of VB-1.",
            await _tool.UpdateBoardCard("VB-1", descriptionAppend: "Decision: keep the removals.", cancellationToken: Ct));
        Assert.Equal("first scope\n\nDecision: keep the removals.", (await _store.FindCardAsync(_project, "VB-1", Ct))!.Description);
        Assert.StartsWith("FAIL: Pass either description (replace) or descriptionAppend (append), not both.",
            await _tool.UpdateBoardCard("VB-1", description: "x", descriptionAppend: "y", cancellationToken: Ct));
        Assert.StartsWith("FAIL: Nothing to append.", await _tool.UpdateBoardCard("VB-1", descriptionAppend: "  ", cancellationToken: Ct));

        // Append plus an INVALID field: nothing lands. The append must not survive a rejected
        // request, or a retry with the corrected field duplicates the text.
        Assert.StartsWith("FAIL: Priority must be one of", await _tool.UpdateBoardCard("VB-1", descriptionAppend: "lost", priority: "urgent", cancellationToken: Ct));
        var untouched = (await _store.FindCardAsync(_project, "VB-1", Ct))!;
        Assert.DoesNotContain("lost", untouched.Description);

        // Append plus a valid field: both land in one transaction, and the reply describes the field update.
        Assert.Equal("Updated VB-1: A (high)", await _tool.UpdateBoardCard("VB-1", descriptionAppend: "more", priority: "high", cancellationToken: Ct));
        var card = (await _store.FindCardAsync(_project, "VB-1", Ct))!;
        Assert.EndsWith("\n\nmore", card.Description);
        Assert.Equal("high", card.Priority);
    }

    [Fact]
    public async Task DescriptionAppend_UsesCurrentTextInsideTheWriteTransaction()
    {
        await _tool.CreateBoardCard("A", "first scope", cancellationToken: Ct);
        // A second writer (the dashboard) changes the description between this tool's read and its write.
        var racing = new Mock<IBoardStore>(MockBehavior.Strict);
        var calls = 0;
        racing.Setup(s => s.FindCardAsync(_project, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>(async (project, key, ct) =>
            {
                var card = await _store.FindCardAsync(project, key, ct);
                if (Interlocked.Increment(ref calls) == 1)
                    await _store.UpdateCardAsync(project, card!.Id, new BoardCardPatch(Description: "dashboard edit"), ct);
                return card;
            });
        racing.Setup(s => s.UpdateCardAsync(_project, It.IsAny<string>(), It.IsAny<BoardCardPatch>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, BoardCardPatch, CancellationToken>((project, id, patch, ct) => _store.UpdateCardAsync(project, id, patch, ct));
        racing.Setup(s => s.GetCardDetailAsync(_project, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((project, id, ct) => _store.GetCardDetailAsync(project, id, ct));
        var service = new BoardService(racing.Object, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());

        var updated = await service.UpdateCardAsync(_project, "VB-1", new UpdateBoardCardRequest(DescriptionAppend: "agent note"), Ct);
        // The transactional append uses the current text, even though the service read an older value.
        Assert.Equal("dashboard edit\n\nagent note", updated!.Description);
    }

    [Fact]
    public async Task AddBoardAttachment_StoresMarkdownAndText_AndLinksTheAgent()
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        _resolver.CurrentSessionId = "sess-writer";

        var reply = await _tool.AddBoardAttachment("findings.md", "# Findings\n\nThree candidates.\n", "VB-1", Ct);
        Assert.Matches(@"^Attached findings\.md \(att_[0-9a-f]{12}, 30 bytes\) to VB-1 as Agent\.$", reply);
        Assert.StartsWith("FAIL: Agent attachments must be Markdown or TXT", await _tool.AddBoardAttachment("tool.exe", "MZ", "VB-1", Ct));
        Assert.StartsWith("FAIL: The attachment text cannot be empty.", await _tool.AddBoardAttachment("empty.txt", "  ", "VB-1", Ct));

        var card = await _tool.GetBoardCard("VB-1", cancellationToken: Ct);
        Assert.Contains("findings.md (text/markdown, 30 bytes)", card);
        var attachmentId = System.Text.RegularExpressions.Regex.Match(card, @"(att_[0-9a-f]{12}): findings\.md").Groups[1].Value;
        var text = await _tool.ReadBoardAttachment(attachmentId, "VB-1", cancellationToken: Ct);
        Assert.EndsWith("# Findings\n\nThree candidates.\n", text);

    }

    [Fact]
    public async Task ABusyDatabase_IsReportedAsRetryable_NotAsSeeTheLog()
    {
        var busy = new Mock<IBoardService>(MockBehavior.Strict);
        busy.Setup(b => b.FindCardAsync(_project, "VB-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BoardCardRecord("card_1", _project, 1, "col", 0, "A", "", null, "medium", null, [], false, 0, DateTime.UtcNow, DateTime.UtcNow));
        busy.Setup(b => b.AddCommentAsync(_project, "card_1", It.IsAny<BoardAuthor>(), "hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SqliteException("SQLite Error 5: 'database is locked'.", 5));
        var tool = new BoardTool(busy.Object, _resolver, _store);

        var reply = await tool.AddBoardComment("hello", "VB-1", Ct);
        Assert.StartsWith("FAIL: could not add the comment: the VibeRails database is busy", reply);
        Assert.Contains("Nothing was saved. Retry the same call in a few seconds.", reply);
        Assert.DoesNotContain("See the VibeRails log", reply);
    }

    [Fact]
    public async Task ListBoards_AndTheBoardArgument_ScopeTheListing()
    {
        Assert.Equal("Created VB-1: On main", await _tool.CreateBoardCard("On main", cancellationToken: Ct));
        var sprint = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Sprint 2"), Ct);

        var boards = await _tool.ListBoards(Ct);
        Assert.Contains("- Main (id brd_", boards);
        Assert.Contains("1 card; lanes: Backlog → Ready → Build → Review → Done; current)", boards);
        Assert.Contains($"- Sprint 2 (id {sprint.Id}, 0 cards; lanes:", boards);

        // By name, case-insensitively, or by id; unknown boards fail readably.
        Assert.Equal("Created VB-2: On sprint", await _tool.CreateBoardCard("On sprint", column: "build", board: "sprint 2", cancellationToken: Ct));
        Assert.Equal("VB-2 [Build] [Task] (medium) On sprint", await _tool.ListBoardCards(board: sprint.Id, cancellationToken: Ct));
        Assert.Equal("VB-1 [Backlog] [Task] (medium) On main", await _tool.ListBoardCards(cancellationToken: Ct));
        Assert.StartsWith("FAIL: board not found: Nowhere", await _tool.ListBoardCards(board: "Nowhere", cancellationToken: Ct));
        Assert.Contains("(board Sprint 2):", await _tool.ListBoardColumns("Sprint 2", Ct));

        // A card's own board is reported, and lane names resolve on that board.
        var card = await _tool.GetBoardCard("VB-2", cancellationToken: Ct);
        Assert.Contains("\nBoard: Sprint 2\nLanes: Backlog → Ready → Build → Review → Done\n", card);
        Assert.StartsWith("Moved VB-2 to Review", await _tool.MoveBoardCard("VB-2", "review", cancellationToken: Ct));
        Assert.Equal(sprint.Id, (await _service.FindCardAsync(_project, "VB-2", Ct))!.BoardId);

        // A terminal launched for a card on the sprint board defaults to that board.
        _resolver.CurrentSessionId = "11111111-2222-3333-4444-555555555555";
        await _store.LinkSessionAsync(_project, (await _service.FindCardAsync(_project, "VB-2", Ct))!.Id, _resolver.CurrentSessionId!, null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        Assert.Equal("VB-2 [Review] [Task] (medium) On sprint", await _tool.ListBoardCards(cancellationToken: Ct));
        Assert.Equal("Created VB-3: Sibling", await _tool.CreateBoardCard("Sibling", cancellationToken: Ct));
        Assert.Equal(sprint.Id, (await _service.FindCardAsync(_project, "VB-3", Ct))!.BoardId);
        Assert.Contains("; current)", (await _tool.ListBoards(Ct)).Split('\n').Single(line => line.Contains("Sprint 2")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateBoardNames_RequireAnIdWithoutWritingToEitherBoard(bool duplicateByRename)
    {
        var first = await _service.CreateBoardAsync(_project, new CreateBoardRequest("Sprint"), Ct);
        var second = await _service.CreateBoardAsync(_project, new CreateBoardRequest(duplicateByRename ? "Other" : "sPRINT"), Ct);
        if (duplicateByRename)
            await _service.UpdateBoardAsync(_project, second.Id, new UpdateBoardRequest("sPRINT"), Ct);

        var listing = await _tool.ListBoardCards(board: "sprint", cancellationToken: Ct);
        Assert.Contains("ambiguous", listing);
        Assert.Contains("Use a board ID from list_boards", listing);
        var creation = await _tool.CreateBoardCard("Wrong target", board: "SPRINT", cancellationToken: Ct);
        Assert.Contains("ambiguous", creation);
        Assert.Empty((await _service.GetCardsAsync(_project, Ct, first.Id)).Cards);
        Assert.Empty((await _service.GetCardsAsync(_project, Ct, second.Id)).Cards);

        Assert.Equal("Created VB-1: First", await _tool.CreateBoardCard("First", board: first.Id, cancellationToken: Ct));
        Assert.Equal("Created VB-2: Second", await _tool.CreateBoardCard("Second", board: second.Id, cancellationToken: Ct));
        Assert.Equal("First", Assert.Single((await _service.GetCardsAsync(_project, Ct, first.Id)).Cards).Title);
        Assert.Equal("Second", Assert.Single((await _service.GetCardsAsync(_project, Ct, second.Id)).Cards).Title);
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
