using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using Moq;
using Tests.Services.BertV2;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-search-" + Guid.NewGuid().ToString("N"));
    private readonly string connectionString;
    private readonly BoardStore store;
    private readonly Mock<IBertV2BgeEmbedder> embedder = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSearchTests()
    {
        Directory.CreateDirectory(root);
        connectionString = $"Data Source={Path.Combine(root, "fixture.db")};Pooling=False";
        store = new(connectionString, connectionString);
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Throws(new IOException("No model"));
    }

    private BoardSearchService Search() => new(SearchTestIndex.Build(store, root, embedder.Object), new ExpandedModel(embedder.Object), NullLogger<BoardSearchService>.Instance);
    private async Task<BoardCardRecord> Card(string title = "Task", string description = "", string? project = null)
    {
        project ??= root;
        await store.EnsureDefaultColumnsAsync(project, Ct);
        return await store.CreateCardAsync(project, new(null, title, description, "base:codex", "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task SearchesAllLocalBoardsWithCurrentProjectFirstAndOwningMetadata()
    {
        var local = await Card("Authentication local");
        var foreign = await Card("Authentication foreign", project: root + "-other");
        var hits = await Search().SearchAsync(Path.Combine(root, "."), "authentication", ct: Ct);
        Assert.Equal(new[] { local.Id, foreign.Id }, hits.Select(hit => hit.Id));
        Assert.True(hits[0].IsCurrentProject);
        Assert.False(hits[1].IsCurrentProject);
        Assert.Equal(foreign.ProjectPath, hits[1].ProjectPath);
        Assert.Equal(foreign.BoardId, hits[1].BoardId);
        Assert.NotEmpty(hits[1].BoardName);
        Assert.NotEmpty(hits[1].ColumnName);
    }

    [Fact]
    public async Task ExactForeignPermanentKeysWinAndCurrentShortAliasesKeepTheirMeaning()
    {
        var local = await Card();
        var foreign = await Card(project: root + "-other");
        Assert.Equal(foreign.Id, (await Search().SearchAsync(root, foreign.Key, 1, ct: Ct)).Single().Id);
        Assert.Equal(local.Id, (await Search().SearchAsync(root, "VB-1", 1, ct: Ct)).Single().Id);
        Assert.Equal(foreign.Id, (await Search().SearchAsync(root, foreign.Id, 1, ct: Ct)).Single().Id);
    }

    [Fact]
    public async Task ForeignLegacyKeyCannotOutrankTheCurrentProjectsShortAlias()
    {
        var local = await Card();
        var foreign = await Card(project: root + "-other");
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE BoardCards SET CardKey=NULL, DisplayId=NULL WHERE Id=$card;
                UPDATE BoardProjectKeys SET Prefix='VB' WHERE ProjectPath=$project;
                """;
            command.Parameters.AddWithValue("$card", foreign.Id);
            command.Parameters.AddWithValue("$project", foreign.ProjectPath);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var hits = await Search().SearchAsync(root, "VB-1", ct: Ct);
        Assert.Equal(local.Id, hits[0].Id);
        Assert.Contains(hits, hit => hit.Id == foreign.Id && hit.Key == "VB-1");
        Assert.Equal(foreign.Id, Assert.Single(await Search().SearchAsync(root, foreign.Id, 1, ct: Ct)).Id);
    }

    [Fact]
    public async Task SearchesLongDescriptionsCommentsLegacyNotesAndHandoffsButNeverHistory()
    {
        var card = await Card(description: new string('x', 8000) + " ultraviolet");
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "A comment about cobalt", Ct);
        await store.SaveHandoffAsync(root, card.Id, new("Finished", "", "Emerald validation", "", []), BoardAuthor.User(), Ct);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BoardComments(Id,CardId,AuthorKind,AuthorLabel,Body,CreatedUTC,Kind)
                VALUES('old-note',$card,'user','You','legacy saffron note',$time,'note'),
                    ('old-change',$card,'user','You','sensitive obsolete description',$time,'change');
                """;
            command.Parameters.AddWithValue("$card", card.Id);
            command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(Ct);
        }
        foreach (var query in new[] { "ultraviolet", "cobalt", "saffron", "emerald" })
        {
            var hit = Assert.Single(await Search().SearchAsync(root, query, ct: Ct));
            Assert.Equal(card.Id, hit.Id);
            Assert.Contains(query, hit.Snippet, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(await Search().SearchAsync(root, "obsolete", ct: Ct));
    }

    [Fact]
    public async Task KeywordFallbackFindsIdentifiersLongerThanASemanticPassage()
    {
        var identifier = "identifier" + new string('x', 490);
        var descriptionCard = await Card("Description match", "Before " + identifier + " after");
        var discussionCard = await Card("Discussion match");
        await store.AddCommentAsync(root, discussionCard.Id, BoardAuthor.User(), "Before " + identifier + " after", Ct);
        var hits = await Search().SearchAsync(root, identifier, ct: Ct);
        Assert.Equal(new[] { descriptionCard.Id, discussionCard.Id }.Order(), hits.Select(hit => hit.Id).Order());
        Assert.All(hits, hit => Assert.Equal("keyword", hit.MatchKind));
    }

    [Fact]
    public async Task SemanticDiscoveryCachesVectorsAndContentChangesInvalidateWithoutTimestampReliance()
    {
        var card = await Card("Maintenance", "Vehicle wheel replacement");
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns<string>(text =>
            text.Contains("wheel", StringComparison.OrdinalIgnoreCase) || text.Contains("tyres", StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f] : [0f, 1f]);
        var result = Assert.Single(await Search().SearchAsync(root, "tyres", ct: Ct));
        Assert.Equal(card.Id, result.Id);
        Assert.Equal("semantic", result.MatchKind);
        Assert.True(SearchTestIndex.Build(store, root).GetProgress().Embedded > 0);
        // An older binary can change content without knowing the new index or touching UpdatedUTC.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET Description='Circuit inspection' WHERE Id=$card";
            command.Parameters.AddWithValue("$card", card.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        Assert.Empty(await Search().SearchAsync(root, "tyres", ct: Ct));
        Assert.Equal(card.Id, Assert.Single(await Search().SearchAsync(root, "circuit", ct: Ct)).Id);
    }

    [Fact]
    public async Task DeletedDiscussionAndCardsCannotMatchRetainedVectors()
    {
        var card = await Card();
        var comment = (await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Vehicle wheel replacement", Ct))!;
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns<string>(text =>
            text.Contains("wheel") || text == "tyres" ? [1f, 0f] : [0f, 1f]);
        Assert.Single(await Search().SearchAsync(root, "tyres", ct: Ct));
        await store.DeleteCommentAsync(root, card.Id, comment.Id, BoardAuthor.User(), Ct);
        Assert.Empty(await Search().SearchAsync(root, "tyres", ct: Ct));
        await store.DeleteCardAsync(root, card.Id, Ct);
        Assert.Empty(await Search().SearchAsync(root, "", ct: Ct));
        Assert.Empty(await store.GetSearchDocumentsAsync(0, Ct));
    }

    [Fact]
    public async Task QueriesNeverAdvanceBackgroundWorkAndKeywordsCoverPendingChunks()
    {
        await Card(description: new string('x', 70000) + " distantneedle");
        var index = SearchTestIndex.Build(store, root);
        var service = new BoardSearchService(index, new ExpandedModel(embedder.Object), NullLogger<BoardSearchService>.Instance);
        var before = index.GetProgress();
        Assert.Single(await service.SearchAsync(root, "distantneedle", ct: Ct));
        Assert.Single(await service.SearchAsync(root, "distantneedle", ct: Ct));
        Assert.Equal(before, index.GetProgress());
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns([1f, 0f]);
        SearchTestIndex.Embed(index, embedder.Object);
        Assert.True(index.GetProgress().Embedded > 0);
        Assert.Equal(0, index.GetProgress().PendingSources);
    }

    [Fact]
    public async Task ExclusionsApplyBeforeResultLimitAndEmptyQueriesNeedNoModel()
    {
        var keep = await Card("Keep");
        var excluded = await Card("Exclude");
        var source = await Card("Source");
        var service = new BoardSearchService(SearchTestIndex.Build(store, root), embedder.Object, NullLogger<BoardSearchService>.Instance);
        var result = Assert.Single(await service.SearchAsync(root, "", 1, source.Id, Ct, [excluded.Id]));
        Assert.Equal(keep.Id, result.Id);
        embedder.Verify(model => model.GenerateEmbedding(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task QueryLengthAndCancellationAreBounded()
    {
        await Assert.ThrowsAsync<BoardValidationException>(() => Search().SearchAsync(root, new string('x', 1001), ct: Ct));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search().SearchAsync(root, "abc", ct: cancelled.Token));
    }

    [Fact]
    public async Task SupersededQueuedSearchDoesNotSpendModelWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns<string>(text =>
        {
            Interlocked.Increment(ref calls);
            if (text == "first") { entered.TrySetResult(); release.Wait(Ct); }
            return [0f, 0f];
        });
        var first = Task.Run(() => Search().SearchAsync(root, "first", ct: Ct), Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var queued = Search().SearchAsync(root, "second", ct: cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public async Task EmptyPickerReadsOnlyBoundedMetadata()
    {
        var card = await Card(description: new string('x', 10000));
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Unneeded discussion", Ct);
        var document = Assert.Single(await store.GetSearchDocumentsAsync(0, Ct, includeContent: false));
        Assert.Equal(2, document.Sources.Count);
        Assert.True(document.Sources[1].Text.Length <= 260);
        Assert.DoesNotContain(document.Sources, source => source.Text.Contains("discussion"));
    }

    [Fact]
    public async Task CursorPagingDoesNotSkipRemainingCardsAfterConcurrentDeletion()
    {
        for (var index = 0; index < 101; index++) await Card("Task " + index);
        var first = await store.GetSearchDocumentsAsync(0, Ct, includeContent: false);
        Assert.Equal(100, first.Count);
        await store.DeleteCardAsync(root, first[0].Id, Ct);
        var last = Assert.Single(await store.GetSearchDocumentsAsync(0, Ct, includeContent: false, afterCardId: first[^1].Id));
        Assert.DoesNotContain(first, document => document.Id == last.Id);
        Assert.Equal(50, (await Search().SearchAsync(root, "", ct: Ct)).Select(hit => hit.Id).Distinct().Count());
    }

    [Fact]
    public async Task DenseAndUnicodePassagesFitRealTokenizerAndCoverEverySourceCharacter()
    {
        var title = new string('!', 95) + "🙂" + new string('?', 203);
        var description = string.Concat(Enumerable.Repeat("你!" + new string('?', 381) + "🙂", 20)) + " wheel replacement";
        var card = await Card(title, description);
        var comment = (await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), description, Ct))!;
        var document = Assert.Single(await store.GetSearchDocumentsAsync(0, Ct));
        var chunker = new SearchTextChunker(Path.Combine(BertV2TestAssets.GetBundledModelDirectory(), "vocab.txt"));
        foreach (var source in document.Sources)
        {
            var chunks = chunker.Split(source.Text, source.Title);
            Assert.NotEmpty(chunks);
            Assert.All(chunks, chunk => Assert.InRange(chunker.CountTokens(chunk), 1, 512));
            // Every source tail survives, including dense Unicode and long title text.
            Assert.EndsWith(source.Text[^Math.Min(20, source.Text.Length)..], chunks[^1]);
            Assert.All(chunks, chunk => Assert.DoesNotContain('\uFFFD', chunk));
            var covered = new bool[source.Text.Length];
            foreach (var chunk in chunker.SplitChunks(source.Text, source.Title))
            {
                Assert.EndsWith(source.Text.Substring(chunk.Start, chunk.Length), chunk.Text);
                Array.Fill(covered, true, chunk.Start, chunk.Length);
            }
            Assert.All(covered, Assert.True);
        }
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns<string>(text =>
            text == "tyres" || text.Contains("wheel replacement", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f]);
        await Search().SearchAsync(root, "tyres", ct: Ct);
        var hit = Assert.Single(await Search().SearchAsync(root, "tyres", ct: Ct));
        Assert.Equal(card.Id, hit.Id);
        Assert.Equal("semantic", hit.MatchKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrongForeignMatchSurvivesMoreThanALimitOfWeakLocalMatches(bool keywordMatch)
    {
        const string query = "Replace a flat tire";
        for (var index = 0; index < 51; index++) await Card("Add dark mode " + index, "Use darker menu colors");
        var foreign = await Card("Wheel repair", keywordMatch ? query : "Swap damaged wheel", root + "-other");
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Returns<string>(text =>
        {
            if (text == query) return [1f, 0f];
            var similarity = text.Contains("Wheel repair") || text.Contains("Swap damaged") ? .942397f : .532783f;
            return [similarity, MathF.Sqrt(1 - similarity * similarity)];
        });
        var hits = await Search().SearchAsync(root, query, 50, ct: Ct);
        Assert.Equal(50, hits.Count);
        Assert.Equal(foreign.Id, hits[0].Id);
        Assert.Equal(keywordMatch ? "keyword+semantic" : "semantic", hits[0].MatchKind);
        Assert.All(hits.Skip(1), hit => Assert.True(hit.IsCurrentProject));
    }

    [Fact]
    public async Task KeywordOnlyStrongForeignMatchOutranksManyWeakLocalMatches()
    {
        const string query = "Replace a flat tire";
        for (var index = 0; index < 51; index++) await Card("Tire menu color " + index, "Use darker colors");
        var foreign = await Card("Repair plan", query, root + "-other");
        var hits = await Search().SearchAsync(root, query, 50, ct: Ct);
        Assert.Equal(50, hits.Count);
        Assert.Equal(foreign.Id, hits[0].Id);
        Assert.Equal("keyword", hits[0].MatchKind);
    }

    [Fact]
    public async Task ProjectScopeFiltersBeforeGlobalRankingAndLimit()
    {
        const string query = "Replace a flat tire";
        var local = await Card("Tire task");
        for (var index = 0; index < 51; index++) await Card(query, project: root + "-other");
        var global = await Search().SearchAsync(root, query, ct: Ct);
        Assert.Equal(50, global.Count);
        Assert.All(global, hit => Assert.False(hit.IsCurrentProject));
        var scoped = Assert.Single(await Search().SearchAsync(root, query, ct: Ct, projectScope: Path.Combine(root, ".")));
        Assert.Equal(local.Id, scoped.Id);
        Assert.True(scoped.IsCurrentProject);
    }

    private sealed class ExpandedModel(IBertV2BgeEmbedder model) : IBertV2BgeEmbedder
    {
        public float[] GenerateEmbedding(string text) => SearchTestIndex.Expand(model.GenerateEmbedding(text));
        public void Dispose() { }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
