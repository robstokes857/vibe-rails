using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The board's SQLite store against a real temp-file database: key allocation, dense positions,
/// lane deletion, cascades and project isolation — the invariants board-controller.js relies on.
/// </summary>
public sealed class BoardStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-store-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly string _project;
    private readonly string _otherProject;

    public BoardStoreTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project-a");
        _otherProject = Path.Combine(_root, "project-b");
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DefaultLanes_AreSeededOnce_PerProject()
    {
        Assert.True(await _store.EnsureDefaultColumnsAsync(_project, Ct));
        Assert.False(await _store.EnsureDefaultColumnsAsync(_project, Ct));
        var columns = await _store.GetColumnsAsync(_project, Ct);
        Assert.Equal(BoardStore.DefaultLanes.Select(l => l.Name), columns.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(0, columns.Count), columns.Select(c => c.Position));

        // Another project starts empty until it asks.
        Assert.Empty(await _store.GetColumnsAsync(_otherProject, Ct));
    }

    [Fact]
    public async Task Keys_AreAllocatedPerProject_AndLookupsAcceptIdOrKey()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        await _store.EnsureDefaultColumnsAsync(_otherProject, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("First"), Ct);
        var second = await _store.CreateCardAsync(_project, NewCard("Second") with { Type = BoardCardTypes.Bug }, Ct);
        var elsewhere = await _store.CreateCardAsync(_otherProject, NewCard("Elsewhere"), Ct);

        Assert.Equal("VB-1", first.Key);
        Assert.Equal("VB-2", second.Key);
        Assert.Equal("VB-1", elsewhere.Key);
        Assert.Equal(0, first.Position);
        Assert.Equal(1, second.Position);
        Assert.Equal(BoardCardTypes.Task, first.Type);
        Assert.Equal(BoardCardTypes.Bug, second.Type);

        Assert.Equal(second.Id, (await _store.FindCardAsync(_project, "vb-2", Ct))!.Id);
        Assert.Equal(BoardCardTypes.Bug, (await _store.FindCardAsync(_project, "vb-2", Ct))!.Type);
        Assert.Equal(second.Id, (await _store.FindCardAsync(_project, second.Id, Ct))!.Id);
        // Keys never cross projects.
        Assert.Equal(elsewhere.Id, (await _store.FindCardAsync(_otherProject, "VB-1", Ct))!.Id);
        Assert.Null(await _store.FindCardAsync(_otherProject, "VB-2", Ct));
        Assert.Null(await _store.FindCardAsync(_project, first.Id + "x", Ct));

        var retyped = await _store.UpdateCardAsync(_project, first.Id, new BoardCardPatch(Type: BoardCardTypes.Feature), Ct);
        Assert.Equal(BoardCardTypes.Feature, retyped!.Type);
    }

    [Fact]
    public async Task CardTypeMigration_BackfillsLegacyCardsAsTask()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var existing = await _store.CreateCardAsync(_project, NewCard("Legacy"), Ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = "ALTER TABLE BoardCards DROP COLUMN Type; DELETE FROM SchemaMigrations WHERE Component='board' AND Version=3;";
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        var migrated = new BoardStore(_connectionString);
        Assert.Equal(BoardCardTypes.Task, (await migrated.FindCardAsync(_project, existing.Id, Ct))!.Type);
    }

    [Fact]
    public async Task Move_KeepsPositionsDense_InBothLanes()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var backlog = columns[0];
        var build = columns[2];
        var a = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        var b = await _store.CreateCardAsync(_project, NewCard("B"), Ct);
        var c = await _store.CreateCardAsync(_project, NewCard("C"), Ct);

        // Move B to Build at position 0, then A to the end of Build (position past the end clamps).
        var movedB = await _store.MoveCardAsync(_project, b.Id, build.Id, 0, Ct);
        Assert.Equal(build.Id, movedB!.ColumnId);
        Assert.Equal(0, movedB.Position);
        var movedA = await _store.MoveCardAsync(_project, a.Id, build.Id, 99, Ct);
        Assert.Equal(1, movedA!.Position);

        var cards = await _store.GetCardsAsync(_project, Ct);
        Assert.Equal(0, cards.Single(x => x.Id == c.Id).Position);
        Assert.Equal(backlog.Id, cards.Single(x => x.Id == c.Id).ColumnId);
        Assert.Equal([b.Id, a.Id], cards.Where(x => x.ColumnId == build.Id).OrderBy(x => x.Position).Select(x => x.Id));

        // Reorder within the same lane.
        await _store.MoveCardAsync(_project, a.Id, build.Id, 0, Ct);
        cards = await _store.GetCardsAsync(_project, Ct);
        Assert.Equal([a.Id, b.Id], cards.Where(x => x.ColumnId == build.Id).OrderBy(x => x.Position).Select(x => x.Id));

        // Update with a new columnId appends to that lane and renumbers the old one.
        var updated = await _store.UpdateCardAsync(_project, c.Id, new BoardCardPatch(ColumnId: build.Id), Ct);
        Assert.Equal(2, updated!.Position);
        Assert.Empty((await _store.GetCardsAsync(_project, Ct)).Where(x => x.ColumnId == backlog.Id));
    }

    [Fact]
    public async Task Keys_NeverReuseDeletedNumbers_EvenAfterEmptyingAndReopeningBoard()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("First"), Ct);
        var second = await _store.CreateCardAsync(_project, NewCard("Second"), Ct);
        Assert.True(await _store.DeleteCardAsync(_project, second.Id, Ct));

        var reopened = new BoardStore(_connectionString);
        var third = await reopened.CreateCardAsync(_project, NewCard("Third"), Ct);
        Assert.Equal("VB-3", third.Key);
        Assert.True(await reopened.DeleteCardAsync(_project, first.Id, Ct));
        Assert.True(await reopened.DeleteCardAsync(_project, third.Id, Ct));
        Assert.Empty(await reopened.GetCardsAsync(_project, Ct));

        var afterEmpty = new BoardStore(_connectionString);
        Assert.Equal("VB-4", (await afterEmpty.CreateCardAsync(_project, NewCard("Fourth"), Ct)).Key);
        Assert.Null(await afterEmpty.FindCardAsync(_project, "VB-1", Ct));
        Assert.Null(await afterEmpty.FindCardAsync(_project, "VB-2", Ct));
        Assert.Null(await afterEmpty.FindCardAsync(_project, "VB-3", Ct));
    }

    [Fact]
    public async Task Keys_MigrateExistingNumbers_BeforeAnyCardCanBeDeleted()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var existing = await _store.CreateCardAsync(_project, NewCard("Existing"), Ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var legacy = connection.CreateCommand();
            // Reproduce the old schema, which predates the migration receipt as well.
            legacy.CommandText = "UPDATE BoardCards SET Number = 42 WHERE Id = $id; DROP TABLE BoardCardSequences; DELETE FROM SchemaMigrations WHERE Component='board';";
            legacy.Parameters.AddWithValue("$id", existing.Id);
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        var migrated = new BoardStore(_connectionString);
        Assert.Equal("VB-42", (await migrated.FindCardAsync(_project, existing.Id, Ct))!.Key);
        Assert.True(await migrated.DeleteCardAsync(_project, existing.Id, Ct));
        var reopened = new BoardStore(_connectionString);
        Assert.Equal("VB-43", (await reopened.CreateCardAsync(_project, NewCard("Next"), Ct)).Key);
    }

    [Fact]
    public async Task Keys_AllocateAtomically_AcrossStoreInstances()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var otherStore = new BoardStore(_connectionString);
        var cancellationToken = Ct;
        var cards = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            (index % 2 == 0 ? _store : otherStore).CreateCardAsync(_project, NewCard($"Card {index}"), cancellationToken),
            cancellationToken)));

        Assert.Equal(Enumerable.Range(1, 12), cards.Select(card => card.Number).Order());
    }

    [Fact]
    public async Task Keys_UseTheSameProjectCaseRules_AsCardLookups()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("First"), Ct);
        Assert.True(await _store.DeleteCardAsync(_project, first.Id, Ct));
        var alternate = _project.ToUpperInvariant();
        await _store.EnsureDefaultColumnsAsync(alternate, Ct);

        var next = await _store.CreateCardAsync(alternate, NewCard("Next"), Ct);
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "VB-2" : "VB-1", next.Key);
    }

    [Fact]
    public async Task DeleteCard_RenumbersItsLane_AndCascadesRails()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var a = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        var b = await _store.CreateCardAsync(_project, NewCard("B"), Ct);
        await _store.AddCommentAsync(_project, a.Id, BoardAuthor.User(), "note", Ct);
        await _store.LinkSessionAsync(_project, a.Id, "session-1", "tab-1", "base:claude", "claude", "Claude · VB-1", BoardSessionRecord.LaunchOrigin, Ct);
        await _store.AddCommitAsync(_project, a.Id, "0123456789abcdef", "Rob", "msg", DateTime.UtcNow, Snapshot(), Ct);
        await _store.AddAttachmentContentAsync(_project, a.Id, "shot.png", "image/png", [137, 80, 78, 71, 13, 10, 26, 10], Ct);

        Assert.True(await _store.DeleteCardAsync(_project, a.Id, Ct));
        Assert.False(await _store.DeleteCardAsync(_project, a.Id, Ct));
        Assert.Equal(0, (await _store.FindCardAsync(_project, b.Id, Ct))!.Position);
        Assert.Null(await _store.FindSessionLinkAsync("session-1", Ct));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        foreach (var table in new[] { "BoardComments", "BoardCardSessions", "BoardCommits", "BoardCommitSnapshots", "BoardAttachments" })
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE CardId = $card";
            count.Parameters.AddWithValue("$card", a.Id);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        }
    }

    [Fact]
    public async Task DeleteColumn_MovesCardsToLeftmostLane_AndRefusesTheLastOne()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var columns = await _store.GetColumnsAsync(_project, Ct);
        var backlog = columns[0];
        var ready = columns[1];
        var inBacklog = await _store.CreateCardAsync(_project, NewCard("stay") with { ColumnId = backlog.Id }, Ct);
        var inReady = await _store.CreateCardAsync(_project, NewCard("move") with { ColumnId = ready.Id }, Ct);

        var result = await _store.DeleteColumnAsync(_project, ready.Id, Ct);
        Assert.Equal(backlog.Id, result!.MovedToColumnId);
        Assert.Equal(1, result.MovedCards);
        var moved = await _store.FindCardAsync(_project, inReady.Id, Ct);
        Assert.Equal(backlog.Id, moved!.ColumnId);
        Assert.Equal(1, moved.Position);
        Assert.Equal(0, (await _store.FindCardAsync(_project, inBacklog.Id, Ct))!.Position);
        Assert.Equal(Enumerable.Range(0, 4), (await _store.GetColumnsAsync(_project, Ct)).Select(c => c.Position));

        for (var remaining = 4; remaining > 1; remaining--)
        {
            var last = (await _store.GetColumnsAsync(_project, Ct)).Last();
            Assert.NotNull(await _store.DeleteColumnAsync(_project, last.Id, Ct));
        }
        var only = Assert.Single(await _store.GetColumnsAsync(_project, Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => _store.DeleteColumnAsync(_project, only.Id, Ct));
        Assert.Null(await _store.DeleteColumnAsync(_project, "col_missing", Ct));
    }

    [Fact]
    public async Task ReorderColumns_RequiresEveryLaneExactlyOnce()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var ids = (await _store.GetColumnsAsync(_project, Ct)).Select(c => c.Id).ToList();
        ids.Reverse();
        var reordered = await _store.ReorderColumnsAsync(_project, ids, Ct);
        Assert.Equal(ids, reordered.Select(c => c.Id));

        await Assert.ThrowsAsync<BoardValidationException>(() => _store.ReorderColumnsAsync(_project, ids.Take(2).ToList(), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.ReorderColumnsAsync(_project, [.. ids, ids[0]], Ct));
    }

    [Fact]
    public async Task Sessions_LinkOnce_AndResolveBackToTheirCardAndProject()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        var link = await _store.LinkSessionAsync(_project, card.Id, "sess-1", null, "env:3:codex", "codex", "Codex", BoardSessionRecord.McpOrigin, Ct);
        Assert.NotNull(link);
        await Assert.ThrowsAsync<BoardConflictException>(() =>
            _store.LinkSessionAsync(_project, card.Id, "sess-1", null, "env:3:codex", "codex", "Codex", BoardSessionRecord.McpOrigin, Ct));

        var found = await _store.FindSessionLinkAsync("sess-1", Ct);
        Assert.Equal(card.Id, found!.CardId);
        Assert.Equal(BoardStore.NormalizeProjectPath(_project), found.ProjectPath);

        var renamed = await _store.RenameSessionAsync(_project, card.Id, "sess-1", "Codex · VB-1", Ct);
        Assert.Equal("Codex · VB-1", renamed!.DisplayName);
        Assert.True(await _store.UnlinkSessionAsync(_project, card.Id, "sess-1", Ct));
        Assert.Null(await _store.FindSessionLinkAsync("sess-1", Ct));
    }

    [Fact]
    public async Task Commits_RejectDuplicates_AndExactShaUnlinks()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        const string sha = "89abcdef0123456789abcdef0123456789abcdef";
        Assert.NotNull(await _store.AddCommitAsync(_project, card.Id, sha, "Rob", "Fix it", DateTime.UtcNow, Snapshot(), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => _store.AddCommitAsync(_project, card.Id, sha, "Rob", "Fix it", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct));
        var reopened = new BoardStore(_connectionString);
        var saved = await reopened.GetCommitSnapshotAsync(_project, card.Id, sha, Ct);
        Assert.Equal(Snapshot().Files, saved!.Files);
        Assert.Equal(1, saved.TotalChanges);
        Assert.Null(await reopened.GetCommitSnapshotAsync(_otherProject, card.Id, sha, Ct));
        Assert.True(await _store.RemoveCommitAsync(_project, card.Id, sha, Ct));
        Assert.Empty(await _store.GetCommitsAsync(_project, card.Id, Ct));
        Assert.Null(await _store.GetCommitSnapshotAsync(_project, card.Id, sha, Ct));
    }

    [Fact]
    public async Task RemoveCommit_DeletesOnlyTheExactSha()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        const string first = "abc1234aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "abc1234bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.NotNull(await _store.AddCommitAsync(_project, card.Id, first, "Rob", "One", DateTime.UtcNow, Snapshot(), Ct));
        Assert.NotNull(await _store.AddCommitAsync(_project, card.Id, second, "Rob", "Two", DateTime.UtcNow, Snapshot(), Ct));

        Assert.False(await _store.RemoveCommitAsync(_project, card.Id, "abc1234", Ct));
        Assert.Equal(2, (await _store.GetCommitsAsync(_project, card.Id, Ct)).Count);

        Assert.True(await _store.RemoveCommitAsync(_project, card.Id, first, Ct));
        var remaining = Assert.Single(await _store.GetCommitsAsync(_project, card.Id, Ct));
        Assert.Equal(second, remaining.Sha);
        Assert.NotNull(await _store.GetCommitSnapshotAsync(_project, card.Id, second, Ct));
        Assert.Null(await _store.GetCommitSnapshotAsync(_project, card.Id, first, Ct));
    }

    [Fact]
    public async Task SnapshotWriteFailure_RollsBackTheCommitLink()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER FailSnapshot BEFORE INSERT ON BoardCommitSnapshots
            BEGIN SELECT RAISE(ABORT, 'Simulated snapshot write failure'); END;
            """;
        await trigger.ExecuteNonQueryAsync(Ct);

        await Assert.ThrowsAsync<SqliteException>(() => _store.AddCommitAsync(
            _project, card.Id, "89abcdef0123456789abcdef0123456789abcdef", "Rob", "Fix", DateTime.UtcNow, Snapshot(), Ct));

        Assert.Empty(await _store.GetCommitsAsync(_project, card.Id, Ct));
    }

    private static SandboxDiffResponse Snapshot() =>
        new([new SandboxDiffFileResponse("a.cs", "csharp", "old\n", "new\n")], 1);

    [Fact]
    public async Task ProjectPaths_AreNormalised_ForLookups()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        var withTrailingSeparator = _project + Path.DirectorySeparatorChar;
        Assert.NotNull(await _store.FindCardAsync(withTrailingSeparator, card.Key, Ct));
        Assert.Single(await _store.GetCardsAsync(withTrailingSeparator, Ct));
    }

    [Fact]
    public async Task Notes_ShareTheCommentsTable_ButNeverTheCommentStream()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("A"), Ct);
        var note = await _store.AddNoteAsync(_project, card.Id, BoardAuthor.Agent("Codex", "codex", "sess-1"), "scratch", Ct);
        var comment = await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "visible", Ct);

        Assert.StartsWith("note_", note!.Id);
        Assert.Equal(BoardCommentKinds.Note, note.Kind);
        Assert.StartsWith("cm_", comment!.Id);
        Assert.Equal(BoardCommentKinds.Comment, comment.Kind);

        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Equal("visible", Assert.Single(detail.Comments).Body);
        Assert.Equal("scratch", Assert.Single(detail.Notes).Body);
        Assert.Equal(1, detail.Card.CommentCount);
        Assert.Equal("scratch", Assert.Single(await _store.GetNotesAsync(_project, card.Key, Ct)).Body);
        Assert.Empty(await _store.GetNotesAsync(_project, "VB-99", Ct));

        // Cascade covers both kinds.
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM BoardComments WHERE CardId = $card";
        count.Parameters.AddWithValue("$card", card.Id);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task ABoardVersion1Database_GainsTheKindColumn_AndKeepsOldCommentsAsComments()
    {
        // A file the previous build left behind: board/1 applied, BoardComments without Kind.
        var legacyRoot = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        var connectionString = $"Data Source={Path.Combine(legacyRoot, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var setup = connection.CreateCommand();
            setup.CommandText = """
                CREATE TABLE SchemaMigrations (Component TEXT NOT NULL, Version INTEGER NOT NULL CHECK (Version > 0), AppliedUTC TEXT NOT NULL, AppliedBy TEXT, PRIMARY KEY (Component, Version));
                INSERT INTO SchemaMigrations VALUES ('board', 1, '2026-09-16T13:38:52Z', NULL);
                CREATE TABLE BoardColumns (Id TEXT PRIMARY KEY, ProjectPath TEXT NOT NULL, Name TEXT NOT NULL, WipLimit INTEGER NULL, Position INTEGER NOT NULL, Color TEXT NOT NULL, CreatedUTC TEXT NOT NULL, UpdatedUTC TEXT NOT NULL);
                CREATE TABLE BoardCards (Id TEXT PRIMARY KEY, ProjectPath TEXT NOT NULL, Number INTEGER NOT NULL, ColumnId TEXT NOT NULL REFERENCES BoardColumns(Id), Position INTEGER NOT NULL, Title TEXT NOT NULL, Description TEXT NOT NULL DEFAULT '', Assignee TEXT NULL, Priority TEXT NOT NULL DEFAULT 'medium', Points INTEGER NULL, Tags TEXT NOT NULL DEFAULT '[]', Blocked INTEGER NOT NULL DEFAULT 0, CreatedUTC TEXT NOT NULL, UpdatedUTC TEXT NOT NULL, UNIQUE(ProjectPath, Number));
                CREATE TABLE BoardCardSequences (ProjectPath TEXT PRIMARY KEY COLLATE NOCASE, LastNumber INTEGER NOT NULL);
                CREATE TABLE BoardComments (Id TEXT PRIMARY KEY, CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE, AuthorKind TEXT NOT NULL, AuthorLabel TEXT NOT NULL, AuthorCli TEXT NULL, SessionId TEXT NULL, Body TEXT NOT NULL, CreatedUTC TEXT NOT NULL);
                CREATE TABLE BoardCardSessions (SessionId TEXT PRIMARY KEY, CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE, TabId TEXT NULL, Selection TEXT NOT NULL, Cli TEXT NOT NULL, DisplayName TEXT NOT NULL, Origin TEXT NOT NULL, CreatedUTC TEXT NOT NULL);
                CREATE TABLE BoardAttachments (Id TEXT PRIMARY KEY, CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE, Name TEXT NOT NULL, MimeType TEXT NOT NULL, Bytes INTEGER NOT NULL, DataUrl TEXT NOT NULL, CreatedUTC TEXT NOT NULL, DeletedUTC TEXT);
                CREATE TABLE BoardAttachmentContents (AttachmentId TEXT PRIMARY KEY REFERENCES BoardAttachments(Id) ON DELETE CASCADE, Content BLOB NOT NULL);
                CREATE TABLE BoardCommits (CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE, Sha TEXT NOT NULL, Author TEXT NOT NULL, Message TEXT NOT NULL, CommittedUTC TEXT NOT NULL, LinkedUTC TEXT NOT NULL, PRIMARY KEY (CardId, Sha));
                CREATE TABLE BoardCommitSnapshots (CardId TEXT NOT NULL, Sha TEXT NOT NULL, SnapshotJson TEXT NOT NULL, PRIMARY KEY (CardId, Sha), FOREIGN KEY (CardId, Sha) REFERENCES BoardCommits(CardId, Sha) ON DELETE CASCADE);
                CREATE TABLE BoardCardOptions (CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE, OptionsJson TEXT NOT NULL);
                CREATE TABLE BoardDescriptionRevisions (CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL, Description TEXT NOT NULL, CreatedUTC TEXT NOT NULL, Source TEXT NOT NULL, AuthorKind TEXT NOT NULL, AuthorLabel TEXT NOT NULL, AuthorCli TEXT NULL, AuthorSessionId TEXT NULL, PRIMARY KEY (CardId, Revision));
                CREATE TABLE BoardDescriptionSessionEvents (CardId TEXT NOT NULL, Revision INTEGER NOT NULL, SessionId TEXT NOT NULL, Kind TEXT NOT NULL, Status TEXT NOT NULL, CreatedUTC TEXT NOT NULL, UpdatedUTC TEXT NOT NULL, Message TEXT NULL, PRIMARY KEY (CardId, Revision, SessionId, Kind), FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE);
                CREATE TABLE BoardDescriptionRevisionAttachments (CardId TEXT NOT NULL, Revision INTEGER NOT NULL, AttachmentId TEXT NOT NULL REFERENCES BoardAttachments(Id) ON DELETE CASCADE, PRIMARY KEY (CardId, Revision, AttachmentId), FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE);
                INSERT INTO BoardColumns VALUES ('col_1', $project, 'Backlog', NULL, 0, '#64748b', '2026-09-16T00:00:00Z', '2026-09-16T00:00:00Z');
                INSERT INTO BoardCards (Id, ProjectPath, Number, ColumnId, Position, Title, CreatedUTC, UpdatedUTC) VALUES ('card_1', $project, 1, 'col_1', 0, 'Old', '2026-09-16T00:00:00Z', '2026-09-16T00:00:00Z');
                INSERT INTO BoardComments VALUES ('cm_1', 'card_1', 'user', 'You', NULL, NULL, 'written before Kind existed', '2026-09-16T00:00:00Z');
                """;
            setup.Parameters.AddWithValue("$project", BoardStore.NormalizeProjectPath(_project));
            await setup.ExecuteNonQueryAsync(Ct);
        }

        var store = new BoardStore(connectionString);
        var detail = (await store.GetCardDetailAsync(_project, "VB-1", Ct))!;
        Assert.Equal("written before Kind existed", Assert.Single(detail.Comments).Body);
        Assert.Equal(BoardCommentKinds.Comment, detail.Comments[0].Kind);
        Assert.Empty(detail.Notes);
        Assert.Equal(1, detail.Card.CommentCount);
        Assert.NotNull(await store.AddNoteAsync(_project, "card_1", BoardAuthor.User(), "new note", Ct));

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var ledger = connection.CreateCommand();
            ledger.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'board' AND Version = 2;";
            Assert.Equal(1L, (long)(await ledger.ExecuteScalarAsync(Ct))!);
        }
        SqliteConnection.ClearPool(new SqliteConnection(connectionString));
    }

    private static NewBoardCard NewCard(string title) =>
        new(null, title, "", null, "medium", null, [], false);

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A still-open WAL handle on Windows; the temp directory is disposable either way.
        }
    }
}
