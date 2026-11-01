using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.BertV2;

public sealed partial class SharedSearchIndexTests
{
    [Fact]
    public async Task UnchangedSweepsWritePresenceWithoutUpdatingWideDocumentsOrSessionChunkCounts()
    {
        var card = await Card("Wide card", new string('x', 50_000));
        SeedInput("session", 1, "searchable input", ended: true);
        await Worker().RunBatchAsync(Ct);
        var metadata = Scalar("SELECT Metadata FROM SearchDocuments WHERE Kind='session'");
        Assert.True(index.ReadDocuments("session").Single().Session!.TotalChunkCount > 0);
        Execute(SearchPath, """
            CREATE TABLE DocumentUpdates(Id TEXT);
            CREATE TRIGGER AuditDocuments AFTER UPDATE ON SearchDocuments BEGIN
                INSERT INTO DocumentUpdates VALUES(new.Id); END;
            """);

        for (var i = 0; i < 2; i++)
        {
            var sweep = await index.ReconcileAsync(board, 25, Ct);
            Assert.Equal((3, 0), (sweep.Documents, sweep.Changed));
        }
        Assert.Equal(0L, Scalar("SELECT count(*) FROM DocumentUpdates"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM SearchDocumentPresence"));
        Assert.Equal(metadata, Scalar("SELECT Metadata FROM SearchDocuments WHERE Kind='session'"));
        Assert.Equal(1L, Scalar("SELECT max(SeenEpoch) FROM SearchDocuments"));

        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Title='Changed title' WHERE Id=$id", ("$id", card.Id));
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(card.Id, Scalar("SELECT Id FROM DocumentUpdates"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM DocumentUpdates"));
        Assert.Equal("Changed title", index.ReadDocuments("board").Single().Board!.Title);
    }

    [Fact]
    public async Task NewProcessesResumePrivateCursorsAcrossRestartEvenInsideTheCooldown()
    {
        await SeedSweepCorpora();
        await index.ReconcileAsync(board, 25, Ct);
        // Force an immediate new pass; subsequent calls still use the normal five-minute cooldown.
        Assert.Equal(6, (await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero)).Documents);
        Assert.Equal(3L, Scalar("SELECT count(*) FROM SearchSweepProgress WHERE Cursor<>''"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM SearchCheckpoints WHERE Cursor<>''"));
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        var resumed = await restarted.ReconcileAsync(board, 25, Ct, minimumInterval: TimeSpan.FromMinutes(5));
        Assert.Equal((15, false), (resumed.Documents, resumed.Incomplete));
        Assert.Equal(21, restarted.GetProgress().Documents);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM SearchSweepProgress"));
        Assert.Equal(0, (await index.ReconcileAsync(board, 25, Ct, minimumInterval: TimeSpan.FromMinutes(5))).Documents);
    }

    [Theory]
    [InlineData("board")]
    [InlineData("input")]
    [InlineData("session")]
    public async Task OlderProcessRestartsANewPartialSweepAndRetainsEveryLiveDocument(string kind)
    {
        await SeedSweepCorpora();
        await index.ReconcileAsync(board, 25, Ct);
        var live = OrderedDocuments(kind);
        await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero);
        Assert.Equal("", Scalar("SELECT Cursor FROM SearchCheckpoints WHERE Kind=$kind", ("$kind", kind)));
        // An old process reads the shared cursor, never the private one. All seven documents
        // must be stamped again before its unchanged SeenEpoch deletion SQL can run.
        Assert.Equal("", LegacyPage(kind, live[..2], finished: false));
        Assert.Equal(SourceCursor(live[1]), LegacyPage(kind, live[2..], finished: true));
        Assert.Equal(7, index.Count(kind));
        Assert.Equal(7L, Scalar("SELECT count(*) FROM SearchDocuments WHERE Kind=$kind AND SeenEpoch=(SELECT Epoch-1 FROM SearchCheckpoints WHERE Kind=$kind)", ("$kind", kind)));
        // Legacy completion invalidates the private epoch. The next new sweep starts fresh.
        Assert.Equal(17, (await index.ReconcileAsync(board, 25, Ct)).Documents);
        Assert.Equal(21, index.GetProgress().Documents);
    }

    [Theory]
    [InlineData("board", false)]
    [InlineData("input", false)]
    [InlineData("session", false)]
    [InlineData("board", true)]
    [InlineData("input", true)]
    [InlineData("session", true)]
    public async Task NewProcessInvalidatesPrivateProgressAfterLegacyPagesOrCompletion(string kind, bool legacyFinishes)
    {
        await SeedSweepCorpora();
        await index.ReconcileAsync(board, 25, Ct);
        var documents = OrderedDocuments(kind);
        await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero);
        // Delete something the new partial sweep already marked. Reusing that token after
        // an intervening old page would incorrectly retain it at the next completion.
        await DeleteSource(documents[0]);
        var live = documents[1..];
        LegacyPage(kind, legacyFinishes ? live : live[..2], legacyFinishes);
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        await restarted.ReconcileAsync(board, 25, Ct);
        Assert.Equal(6, restarted.Count(kind));
        Assert.DoesNotContain(restarted.ReadDocuments(kind), d => d.Id == documents[0].Id);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM SearchDocumentPresence WHERE DocumentId=$id", ("$id", documents[0].Id)));
    }

    [Fact]
    public async Task AdditiveUpgradeAdoptsALegacyPartialSweepWithoutBackfillingDocuments()
    {
        await SeedSweepCorpora();
        await index.ReconcileAsync(board, 25, Ct);
        // Recreate the pre-presence schema in this disposable fixture, with a legacy page
        // committed before the upgrade. No canonical/runtime database is involved.
        Execute(SearchPath, """
            DROP TABLE SearchSweepProgress;
            DROP TABLE SearchDocumentPresence;
            DELETE FROM SchemaMigrations WHERE Component='shared-search' AND Version=2;
            """);
        LegacyPage("board", OrderedDocuments("board")[..2], finished: false);
        SearchDatabaseSchema.Ensure(SearchPath);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM SearchDocumentPresence"));
        Assert.Equal(21L, Scalar("SELECT count(*) FROM SearchDocuments"));
        Assert.Equal(1L, Scalar("PRAGMA user_version"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM SchemaMigrations WHERE Component='shared-search' AND Version=2"));
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        Assert.Equal(21, (await restarted.ReconcileAsync(board, 25, Ct)).Documents);
        Assert.Equal(21, restarted.GetProgress().Documents);
        Assert.Equal(21L, Scalar("SELECT count(*) FROM SearchDocumentPresence"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrExpiredLeaseCannotCommitAPageOrDeleteDocuments(bool replaced)
    {
        for (var i = 0; i < 3; i++) await Card("Card " + i);
        await index.ReconcileAsync(board, 25, Ct);
        await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero);
        var cursor = Scalar("SELECT Cursor FROM SearchSweepProgress WHERE Kind='board'");
        var documents = await board.GetSearchDocumentsAsync(0, Ct);
        var source = new Mock<IBoardStore>();
        source.Setup(b => b.GetSearchDocumentsAsync(0, It.IsAny<CancellationToken>(), true, It.IsAny<string?>(), 25))
            .Returns(() =>
            {
                Execute(SearchPath, replaced
                    ? "UPDATE SearchCheckpoints SET Lease='another-owner' WHERE Kind='board'"
                    : "UPDATE SearchCheckpoints SET LeaseUntil=0 WHERE Kind='board'");
                return Task.FromResult<IReadOnlyList<BoardSearchDocument>>([]);
            });
        var rejected = await index.ReconcileAsync(source.Object, 25, Ct);
        Assert.True(rejected.Incomplete);
        Assert.Equal(0, rejected.Documents);
        Assert.Equal(3, index.Count("board"));
        Assert.Equal(cursor, Scalar("SELECT Cursor FROM SearchSweepProgress WHERE Kind='board'"));
        if (replaced) Assert.Equal("another-owner", Scalar("SELECT Lease FROM SearchCheckpoints WHERE Kind='board'"));
        Execute(SearchPath, "UPDATE SearchCheckpoints SET LeaseUntil=0 WHERE Kind='board'");
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        Assert.Equal(1, (await restarted.ReconcileAsync(board, 25, Ct)).Documents);
        Assert.Equal(documents.Count, restarted.Count("board"));
    }

    [Fact]
    public async Task FailedPageRollsBackPresenceAndResumesBeforeDeletingAbsentDocuments()
    {
        for (var i = 0; i < 4; i++) await Card("Card " + i);
        await index.ReconcileAsync(board, 25, Ct);
        var documents = OrderedDocuments("board");
        await DeleteSource(documents[^1]);
        await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero);
        var cursor = Scalar("SELECT Cursor FROM SearchSweepProgress WHERE Kind='board'");
        Execute(SearchPath, """
            CREATE TRIGGER FailPresence BEFORE UPDATE ON SearchDocumentPresence BEGIN
                SELECT RAISE(ABORT,'injected page failure'); END;
            """);
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(4, index.Count("board"));
        Assert.Equal(cursor, Scalar("SELECT Cursor FROM SearchSweepProgress WHERE Kind='board'"));
        Assert.Contains("injected page failure", index.GetProgress().LastError);
        Execute(SearchPath, "DROP TRIGGER FailPresence; UPDATE SearchCheckpoints SET RetryAt=0");
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        Assert.Equal(1, (await restarted.ReconcileAsync(board, 25, Ct)).Documents);
        Assert.Equal(3, restarted.Count("board"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM SearchDocumentPresence"));
    }

    private async Task SeedSweepCorpora()
    {
        for (var i = 1; i <= 7; i++)
        {
            await Card("Card " + i);
            SeedInput("session" + i, i, "input " + i, ended: true);
        }
    }

    private SearchDocument[] OrderedDocuments(string kind) => kind == "input"
        ? index.ReadDocuments(kind).OrderBy(d => d.Input!.UserInputId).ToArray()
        : index.ReadDocuments(kind).OrderBy(SourceCursor, StringComparer.Ordinal).ToArray();

    private static string SourceCursor(SearchDocument document) => document.Input?.UserInputId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? document.Session?.SessionId ?? document.Id;

    private async Task DeleteSource(SearchDocument document)
    {
        if (document.Kind == "board") await board.DeleteCardAsync(root, document.Id, Ct);
        else if (document.Kind == "input") Execute(State,
            "DELETE FROM InputFileChanges WHERE UserInputId=$id; DELETE FROM UserInputs WHERE Id=$id", ("$id", document.Input!.UserInputId));
        else Execute(State, "UPDATE Sessions SET EndedUTC=NULL WHERE Id=$id", ("$id", document.Session!.SessionId));
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var db = SqliteConnectionFactory.Open($"Data Source={SearchPath}");
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    // Checkpoint, upsert and completion SQL frozen from the pre-VIBE-255 ingestion path.
    // Sources are already present in these fixtures; only the legacy presence protocol is replayed.
    private string LegacyPage(string kind, IReadOnlyList<SearchDocument> page, bool finished)
    {
        using var db = SqliteConnectionFactory.Open($"Data Source={SearchPath}");
        using var tx = db.BeginTransaction();
        void Run(string sql, params (string Name, object? Value)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        var epoch = (long)Scalar("SELECT Epoch FROM SearchCheckpoints WHERE Kind=$kind", ("$kind", kind))!;
        var cursor = (string)Scalar("SELECT Cursor FROM SearchCheckpoints WHERE Kind=$kind", ("$kind", kind))!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Run("UPDATE SearchCheckpoints SET Lease='legacy',LeaseUntil=$until WHERE Kind=$kind AND LeaseUntil<=$now",
            ("$until", now + 120_000), ("$kind", kind), ("$now", now));
        foreach (var document in page)
            Run("""
                INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch)
                VALUES($id,$kind,$project,$session,$metadata,$epoch)
                ON CONFLICT(Id) DO UPDATE SET ProjectPath=excluded.ProjectPath,SessionId=excluded.SessionId,
                    Metadata=CASE WHEN SearchDocuments.Kind='session' THEN
                        json_set(excluded.Metadata,'$.session.totalChunkCount',
                            coalesce(json_extract(SearchDocuments.Metadata,'$.session.totalChunkCount'),0))
                        ELSE excluded.Metadata END,SeenEpoch=excluded.SeenEpoch
                WHERE ProjectPath<>excluded.ProjectPath OR SessionId IS NOT excluded.SessionId
                    OR Metadata<>excluded.Metadata OR SeenEpoch<>excluded.SeenEpoch
                """, ("$id", document.Id), ("$kind", kind), ("$project", document.ProjectPath),
                ("$session", document.Input?.SessionId ?? document.Session?.SessionId),
                ("$metadata", JsonSerializer.Serialize(document with { Sources = [] }, StorageJsonSerializerContext.Default.SearchDocument)), ("$epoch", epoch));
        Run("""
            UPDATE SearchCheckpoints SET Cursor=$cursor,Epoch=$epoch,CompletedUtc=CASE WHEN $finished THEN $utc ELSE CompletedUtc END,
                Error=NULL,Attempts=0,RetryAt=0 WHERE Kind=$kind AND Lease='legacy';
            """, ("$cursor", finished ? "" : SourceCursor(page[^1])), ("$epoch", epoch + (finished ? 1 : 0)),
            ("$finished", finished), ("$utc", DateTime.UtcNow.ToString("O")), ("$kind", kind));
        if (finished) Run("DELETE FROM SearchDocuments WHERE Kind=$kind AND SeenEpoch<>$epoch", ("$kind", kind), ("$epoch", epoch));
        Run("UPDATE SearchCheckpoints SET Lease=NULL,LeaseUntil=0 WHERE Kind=$kind AND Lease='legacy'", ("$kind", kind));
        tx.Commit();
        return cursor;
    }
}
