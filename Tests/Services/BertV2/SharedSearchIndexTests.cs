using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class SharedSearchIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shared-search-" + Guid.NewGuid().ToString("N"));
    private readonly IBoardStore board;
    private readonly SqliteSearchIndexStore index;
    private readonly SearchTextChunker chunker;
    private readonly Model model = new();
    private string State => Path.Combine(root, "state.db");
    private string SearchPath => Path.Combine(root, "search.db");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public SharedSearchIndexTests()
    {
        Directory.CreateDirectory(root);
        StateDatabaseSchema.Ensure($"Data Source={State}");
        board = SqliteStorage.CreateBoardStore(State);
        index = new(SearchPath, State);
        chunker = new(Path.Combine(BertV2TestAssets.GetBundledModelDirectory(), "vocab.txt"));
    }

    private SearchIndexingService Worker(SqliteSearchIndexStore? storage = null, IBertV2BgeEmbedder? embedding = null) =>
        new(storage ?? index, board, () => chunker, () => embedding ?? model);
    private BoardSearchService Finder() => new(index, model, NullLogger<BoardSearchService>.Instance);
    private async Task<BoardCardRecord> Card(string title, string text = "")
    {
        await board.EnsureDefaultColumnsAsync(root, Ct);
        return await board.CreateCardAsync(root, new(null, title, text, null, "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task FreshCorpusIndexesDeepCardSourcesWithoutQueriesAndBothMcpToolsFindThem()
    {
        for (var i = 0; i < 110; i++) await Card("Maintenance " + i, "General scheduled tasks");
        var deep = string.Join(' ', Enumerable.Range(0, 1700).Select(i => "irrelevant" + i)) + " vehicle wheel replacement";
        var relevant = await Card("Important repair", deep);
        await board.AddCommentAsync(root, relevant.Id, BoardAuthor.User(), deep + " final discussion", Ct);
        // Background work runs before any search request, across multiple durable pages.
        for (var i = 0; i < 50; i++)
        {
            await Worker().RunBatchAsync(Ct, 25);
            var progress = index.GetProgress();
            if (progress.Documents == 111 && progress.PendingSources == 0 && progress.Embedded == progress.Chunks) break;
        }
        Assert.Equal(111, index.GetProgress().Documents);
        Assert.Equal(index.GetProgress().Chunks, index.GetProgress().Embedded);
        var before = index.GetProgress();
        var calls = model.Calls;
        var hit = Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.Equal(relevant.Id, hit.Id);
        Assert.Contains("wheel", hit.Snippet);
        Assert.Equal(calls + 1, model.Calls); // query embedding only
        Assert.Equal(before, index.GetProgress());
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        var recall = new BoardRecallService(projects.Object, index, Finder());
        var history = new UnifiedSearchService(model, new BertSearchDbService(index), new BertDocumentResponseMapper());
        Assert.Contains(relevant.Key, await new SessionSearchTool(history, recall).SearchHistory("tyres", cancellationToken: Ct));
        var service = new BoardService(board, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        Assert.Contains(relevant.Key, await new BoardTool(service, projects.Object, board, search: Finder()).SearchBoardCards("tyres", cancellationToken: Ct));
    }

    [Fact]
    public async Task HistoryAndMetadataAreServedAfterCanonicalDatabasesBecomeUnavailable()
    {
        SeedInput("s1", 1, "Repair vehicle wheel replacement", ended: true);
        await Card("Repair", "Vehicle wheel replacement");
        await Worker().RunBatchAsync(Ct, 10);
        var db = new BertSearchDbService(index);
        Assert.Single(db.SearchByText("wheel", 10));
        Assert.Single(db.SearchSessionsByEmbedding(Model.Vector(0), 10));
        SqliteConnection.ClearAllPools();
        // Remove only disposable fixture sources. Query code must not recreate or open them.
        File.Move(State, State + ".offline");
        File.Move(Path.Combine(root, "board.db"), Path.Combine(root, "board.offline"));
        var modified = File.GetLastWriteTimeUtc(SearchPath);
        var response = new UnifiedSearchService(model, db, new BertDocumentResponseMapper()).Search("tyres", 10);
        var hit = Assert.Single(response.Groups[0].Hits);
        Assert.Equal("s1", hit.SessionId);
        Assert.Equal("codex", hit.Cli);
        Assert.Equal(root, hit.WorkingDirectory);
        Assert.Equal("file.cs", Assert.Single(db.GetFileChanges(1)).FilePath);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.False(File.Exists(State));
        Assert.False(File.Exists(Path.Combine(root, "board.db")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(SearchPath));
    }

    [Fact]
    public async Task ReconciliationInvalidatesOldTextBeforeReplacementInferenceAndFencesAnOldClaim()
    {
        var card = await Card("Repair", "Vehicle wheel replacement");
        await Worker().RunBatchAsync(Ct);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Description='circuit inspection' WHERE Id=$id", ("$id", card.Id));
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Empty(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.Single(index.Search("board", "circuit", null, 10));
        var source = Assert.Single(index.ClaimSources("board", 10, SearchIndexVersions.Chunks));
        index.CompleteSource(source, chunker.Split(source.Text, source.Title, Ct), SearchIndexVersions.Chunks);
        var obsolete = Assert.Single(index.ClaimChunks("board", 10, SearchIndexVersions.Model));
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Description='another current task' WHERE Id=$id", ("$id", card.Id));
        await index.ReconcileAsync(board, 25, Ct);
        index.CompleteChunk(obsolete, Model.Vector(0), SearchIndexVersions.Model);
        Assert.Empty(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.Empty(index.Search("board", "circuit", null, 10));
    }

    [Fact]
    public async Task ConcurrentRootsClaimInferenceOnceAndBothCorporaMakeProgress()
    {
        for (var i = 0; i < 12; i++) { await Card("Board " + i, "Text " + i); SeedInput("s1", i + 1, "input text " + i); }
        var second = new SqliteSearchIndexStore(SearchPath, State);
        await Task.WhenAll(Task.Run(() => Worker().RunBatchAsync(Ct, 25), Ct), Task.Run(() => Worker(second).RunBatchAsync(Ct, 25), Ct));
        await Worker().RunBatchAsync(Ct, 25);
        Assert.Equal(12, index.Count("board"));
        Assert.Equal(12, index.Count("input"));
        Assert.True(index.Count("board", true) > 0);
        Assert.True(index.Count("input", true) > 0);
        Assert.Equal(index.GetProgress().Embedded, model.Calls);
    }

    [Fact]
    public async Task FailureIsDurableAndRetryResumesWithoutBlockingKeywordSearch()
    {
        await Card("Useful title", "vehicle wheel replacement");
        var broken = new Mock<IBertV2BgeEmbedder>();
        broken.Setup(m => m.GenerateEmbedding(It.IsAny<string>())).Throws(new IOException("model unavailable"));
        await Worker(embedding: broken.Object).RunBatchAsync(Ct);
        Assert.True(index.GetProgress().Failed > 0);
        Assert.Contains("model unavailable", index.GetProgress().LastError);
        Assert.Single(index.Search("board", "wheel", null, 10));
        var restarted = new SqliteSearchIndexStore(SearchPath, State);
        Assert.Equal(index.GetProgress(), restarted.GetProgress());
        Execute(SearchPath, "UPDATE SearchChunks SET RetryAt=0");
        await Worker(restarted).RunBatchAsync(Ct);
        Assert.Equal(0, restarted.GetProgress().Failed);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
    }

    [Fact]
    public async Task ExpiredClaimsResumeAndDeletedOrHiddenDiscussionStopsMatching()
    {
        var card = await Card("Task");
        var comment = await board.AddCommentAsync(root, card.Id, BoardAuthor.User(), "vehicle wheel replacement", Ct);
        await index.ReconcileAsync(board, 25, Ct);
        var held = index.ClaimSources("board", 1, SearchIndexVersions.Chunks).Single();
        Assert.DoesNotContain(index.ClaimSources("board", 100, SearchIndexVersions.Chunks), work => work.Id == held.Id);
        Execute(SearchPath, "UPDATE SearchSources SET LeaseUntil=0");
        await Worker().RunBatchAsync(Ct, 25);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardComments SET DiscussionHidden=1 WHERE Id=$id", ("$id", comment!.Id));
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Empty(await Finder().SearchAsync(root, "tyres", ct: Ct));
        await board.DeleteCardAsync(root, card.Id, Ct);
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Empty(index.Search("board", "", null, 10));
    }

    [Fact]
    public async Task RebuildRestoresDerivedDataWithoutChangingSourceOrLegacyCache()
    {
        var card = await Card("Repair", "vehicle wheel replacement");
        await board.SaveSearchEmbeddingAsync(root, card.Id, "legacy", "legacy-version", [1, 0], Ct);
        await Worker().RunBatchAsync(Ct);
        var calls = model.Calls;
        await Worker().RunBatchAsync(Ct);
        Assert.Equal(calls, model.Calls);
        Execute(SearchPath, "DELETE FROM vec_search_chunks");
        Assert.True(index.Repair());
        await Worker().RunBatchAsync(Ct);
        Assert.True(model.Calls > calls);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Version FROM BoardSearchEmbeddings";
        Assert.Equal("legacy-version", cmd.ExecuteScalar());
    }

    private void SeedInput(string session, long id, string text, bool ended = false)
    {
        Execute(State, """
            INSERT OR IGNORE INTO Sessions(Id,Cli,WorkingDirectory,StartedUTC,EndedUTC,OwnerPid)
            VALUES($session,'codex',$project,$utc,$end,1);
            INSERT INTO UserInputs(Id,SessionId,Sequence,InputText,TimestampUTC) VALUES($id,$session,$id,$text,$utc);
            INSERT INTO InputFileChanges(UserInputId,FilePath,ChangeType,LinesAdded,LinesDeleted) VALUES($id,'file.cs','modified',1,0);
            """, ("$session", session), ("$id", id), ("$text", text), ("$utc", DateTime.UtcNow.ToString("O")),
            ("$end", ended ? DateTime.UtcNow.ToString("O") : null), ("$project", root));
    }

    [Fact]
    public async Task UnavailableHistoryPreservesItsIndexAndDoesNotBlockBoardProgress()
    {
        SeedInput("s1", 1, "retained history text", ended: true);
        await Worker().RunBatchAsync(Ct);
        // A last read-only pooled handle does not checkpoint WAL when closed. Make the
        // disposable fixture self-contained before moving its main file out and back.
        Execute(State, "PRAGMA wal_checkpoint(TRUNCATE)");
        SqliteConnection.ClearAllPools();
        File.Move(State, State + ".offline");
        var card = await Card("New Board work", "vehicle wheel replacement");
        await Worker().RunBatchAsync(Ct);
        Assert.Equal(1, index.Count("input"));
        Assert.Equal(1, index.Count("session"));
        Assert.Equal(card.Id, Assert.Single(index.Search("board", "wheel", null, 10)).Document.Id);
        Assert.Equal(2, index.GetProgress().Failed);
        Assert.Contains("History source", index.GetProgress().LastError);
        SqliteConnection.ClearAllPools();
        File.Move(State + ".offline", State, overwrite: true);
        Execute(SearchPath, "UPDATE SearchCheckpoints SET RetryAt=0");
        await Worker().RunBatchAsync(Ct);
        Assert.True(index.GetProgress().Failed == 0, index.GetProgress().LastError);
        Assert.Equal(1, index.Count("input"));
        Assert.Equal(1, index.Count("session"));
    }

    [Fact]
    public async Task ModelAndChunkVersionsInvalidateVectorsAndRebuildWithoutSourceChanges()
    {
        await Card("Repair", "vehicle wheel replacement");
        await Worker().RunBatchAsync(Ct);
        Execute(SearchPath, "UPDATE SearchChunks SET ModelVersion='obsolete-model'");
        Assert.Empty(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.Single(index.Search("board", "wheel", null, 10));
        await Worker().RunBatchAsync(Ct);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Execute(SearchPath, "UPDATE SearchSources SET ChunkVersion='obsolete-chunks'");
        Assert.Empty(await Finder().SearchAsync(root, "tyres", ct: Ct));
        await Worker().RunBatchAsync(Ct);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
    }

    [Fact]
    public async Task DeepSessionKeywordHitReadsBackTheMatchingChunkAndWorksBeforeTokenization()
    {
        var text = string.Join(' ', Enumerable.Repeat("Unrelated opening material.", 500)) + " zebracrossing resolution";
        SeedInput("s1", 1, text, ended: true);
        await index.ReconcileAsync(board, 25, Ct);
        var db = new BertSearchDbService(index);
        var pending = Assert.Single(db.SearchSessionsByText("zebracrossing", 10));
        Assert.Contains("zebracrossing", db.GetSessionCapture(pending.DocumentId)!.RawText);
        await Worker().RunBatchAsync(Ct);
        var hit = Assert.Single(db.SearchSessionsByText("zebracrossing", 10));
        Assert.True(BertSessionDocumentId.Parse(hit.DocumentId)!.ChunkIndex > 0);
        Assert.Contains("zebracrossing", hit.RawText);
        Assert.Contains("zebracrossing", db.GetSessionCapture(hit.DocumentId)!.RawText);
        Assert.Equal(hit.DocumentId, db.GetSessionMetadataByDocumentIds([hit.DocumentId])[hit.DocumentId].DocumentId);
    }

    [Fact]
    public async Task MissingDerivedDatabaseRebuildsAutomaticallyFromCanonicalSources()
    {
        SeedInput("s1", 1, "vehicle wheel replacement", ended: true);
        await Card("Repair", "vehicle wheel replacement");
        await Worker().RunBatchAsync(Ct);
        var original = index.GetProgress();
        Execute(SearchPath, "PRAGMA wal_checkpoint(TRUNCATE)");
        SqliteConnection.ClearAllPools();
        File.Delete(SearchPath);
        Assert.Empty(index.Search("board", "wheel", null, 10));
        await Worker().RunBatchAsync(Ct);
        var rebuilt = index.GetProgress();
        Assert.Equal(original.Documents, rebuilt.Documents);
        Assert.Equal(original.Chunks, rebuilt.Embedded);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
        Assert.Single(new BertSearchDbService(index).SearchByText("wheel", 10));
    }

    [Fact]
    public async Task CancelledBatchLeavesNoClaimsAndCanResume()
    {
        await Card("Repair", "vehicle wheel replacement");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => chunker.Split(new string('x', 100000), ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker().RunBatchAsync(cancelled.Token));
        await Worker().RunBatchAsync(Ct);
        Assert.Single(await Finder().SearchAsync(root, "tyres", ct: Ct));
    }

    [Fact]
    public async Task NewCapturesUseNoStateSearchWorkWhileOlderWritersKeepTheirQueue()
    {
        SeedInput("old", 1, "legacy searchable capture");
        var legacy = new SqliteSearchIndexMaintenanceStore($"Data Source={State}");
        await legacy.RepairPendingAsync(100, Ct);
        var repository = new Repository($"Data Source={State}");
        await repository.CreateSessionAsync("new", "codex", null, root, 1);
        await repository.InsertUserInputAsync("new", 1, "current searchable capture", null);
        using var db = new SqliteConnection($"Data Source={State}");
        await db.OpenAsync(Ct);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM UserInputSearchPending";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(Ct));
        cmd.CommandText = "SELECT InputText FROM UserInputSearchDocuments";
        Assert.Equal("legacy searchable capture", await cmd.ExecuteScalarAsync(Ct));
        await Worker().RunBatchAsync(Ct);
        Assert.Equal(2, index.Count("input"));
        cmd.CommandText = "SELECT count(*) FROM UserInputSearchDocuments";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(Ct));
        SeedInput("old", 3, "older writer after upgrade");
        cmd.CommandText = "SELECT UserInputId FROM UserInputSearchPending";
        Assert.Equal(3L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task OneReconciliationSweepsEveryPageAndAnUnchangedCorpusReportsNoChanges()
    {
        for (var i = 0; i < 60; i++) await Card("Card " + i, "Text " + i);
        SeedInput("s1", 1, "first capture", ended: true);
        // 60 cards over three pages, one input and its ended session: one call, no paging loop (VB-2GUR8-187).
        var first = await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal((62, 62, false), (first.Documents, first.Changed, first.Incomplete));
        Assert.Equal(60, index.Count("board"));
        var again = await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal((62, 0, false), (again.Documents, again.Changed, again.Incomplete));
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Description='edited text' WHERE Title='Card 59'");
        var edited = await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal((62, 1, false), (edited.Documents, edited.Changed, edited.Incomplete));
        Assert.Single(index.Search("board", "edited", null, 10));
    }

    [Fact]
    public async Task ABudgetYieldsAfterOnePageAndTheDurableCursorResumesTheSameEpoch()
    {
        for (var i = 0; i < 30; i++) await Card("Card " + i, "Text " + i);
        var partial = await index.ReconcileAsync(board, 25, Ct, TimeSpan.Zero);
        Assert.Equal((25, true), (partial.Documents, partial.Incomplete));
        Assert.Equal(25, index.Count("board"));
        var resumed = await index.ReconcileAsync(board, 25, Ct, TimeSpan.Zero);
        Assert.Equal((5, false), (resumed.Documents, resumed.Incomplete));
        // Finishing the epoch removed nothing: every card was marked by the same epoch across both calls.
        Assert.Equal(30, index.Count("board"));
    }

    [Fact]
    public async Task BatchAsksForAFollowUpOnlyWhileItWasCutShortWithWorkCompleting()
    {
        await Card("Repair", "vehicle wheel replacement");
        // A single round per kind cannot drain the sources and their chunks: a follow-up is due.
        Assert.True(await Worker().RunBatchAsync(Ct, 1));
        var batches = 1;
        while (await Worker().RunBatchAsync(Ct, 1)) Assert.True(++batches < 20, "the backlog never drained");
        var progress = index.GetProgress();
        Assert.Equal(0, progress.PendingSources);
        Assert.Equal(progress.Chunks, progress.Embedded);
        Assert.False(await Worker().RunBatchAsync(Ct));
        // Rounds that only fail wait for their retry delay; they must not spin the follow-up cadence.
        Execute(SearchPath, "UPDATE SearchChunks SET ModelVersion='obsolete-model'");
        var broken = new Mock<IBertV2BgeEmbedder>();
        broken.Setup(m => m.GenerateEmbedding(It.IsAny<string>())).Throws(new IOException("model unavailable"));
        Assert.False(await Worker(embedding: broken.Object).RunBatchAsync(Ct, 1));
        Assert.True(index.GetProgress().Failed > 0);
    }

    internal static void Execute(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var db = BertVectorDatabase.Open(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private sealed class Model : IBertV2BgeEmbedder
    {
        public int Calls;
        public float[] GenerateEmbedding(string text)
        {
            Interlocked.Increment(ref Calls);
            return Vector(text.Contains("wheel", StringComparison.OrdinalIgnoreCase) || text == "tyres" ? 0 : 1);
        }
        public static float[] Vector(int position) { var vector = new float[384]; vector[position] = 1; return vector; }
        public void Dispose() { }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
