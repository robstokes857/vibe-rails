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
        var second = await _store.CreateCardAsync(_project, NewCard("Second"), Ct);
        var elsewhere = await _store.CreateCardAsync(_otherProject, NewCard("Elsewhere"), Ct);

        Assert.Equal("VB-1", first.Key);
        Assert.Equal("VB-2", second.Key);
        Assert.Equal("VB-1", elsewhere.Key);
        Assert.Equal(0, first.Position);
        Assert.Equal(1, second.Position);

        Assert.Equal(second.Id, (await _store.FindCardAsync(_project, "vb-2", Ct))!.Id);
        Assert.Equal(second.Id, (await _store.FindCardAsync(_project, second.Id, Ct))!.Id);
        // Keys never cross projects.
        Assert.Equal(elsewhere.Id, (await _store.FindCardAsync(_otherProject, "VB-1", Ct))!.Id);
        Assert.Null(await _store.FindCardAsync(_otherProject, "VB-2", Ct));
        Assert.Null(await _store.FindCardAsync(_project, first.Id + "x", Ct));
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
            // Reproduce the old schema with a surviving card numbered above one.
            legacy.CommandText = "UPDATE BoardCards SET Number = 42 WHERE Id = $id; DROP TABLE BoardCardSequences;";
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
        await _store.AddAttachmentAsync(_project, a.Id, "shot.png", "image/png", 10, "data:image/png;base64,AAAA", Ct);

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
