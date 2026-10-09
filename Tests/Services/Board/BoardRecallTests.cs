using Tests.Services.BertV2;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardRecallTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-recall-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly Mock<IBoardProjectResolver> projects = new();
    private readonly Mock<IBertV2BgeEmbedder> embedder = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardRecallTests()
    {
        Directory.CreateDirectory(root);
        var connection = $"Data Source={Path.Combine(root, "fixture.db")};Pooling=False";
        store = new(connection, connection);
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        projects.SetupGet(p => p.GitWorkingDirectory).Returns(root);
        embedder.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Returns<string>(s =>
            s.Contains("overflow", StringComparison.OrdinalIgnoreCase) ? [1f, 0f] : [0f, 1f]);
    }

    private BoardRecallService Recall(Func<IBertV2BgeEmbedder>? factory = null, IBertSearchDbService? history = null)
    {
        var index = SearchTestIndex.Build(store, root);
        var search = new BoardSearchService(index, factory ?? (() => embedder.Object), NullLogger<BoardSearchService>.Instance);
        return new(projects.Object, index, search, history);
    }
    private async Task<BoardCardRecord> Card(string title = "Description overflow", string? project = null)
    {
        project ??= root;
        await store.EnsureDefaultColumnsAsync(project, Ct);
        return await store.CreateCardAsync(project, new(null, title, "Original task", "base:codex", "medium", null, [], false), Ct);
    }

    [Theory]
    [InlineData("Tell me about vb-10 and VB-20.", "VB-10,VB-20")]
    [InlineData("(VB-A12CD-10), VIBE-23?", "VB-A12CD-10,VIBE-23")]
    [InlineData("version 10, 1.2.3; card 10; VB-; VB-10x; xVB-10z; VB-10-more; VB-10.2; dir/VB-10.cs", "")]
    public void IdentifiersHaveWholeTokenBoundaries(string query, string expected) =>
        Assert.Equal(expected, string.Join(',', BoardRecallService.ExtractKeys(query)));

    [Fact]
    public async Task ExactKeysBeatHistoryAndForeignPermanentKeysAreExplicitlyLabeled()
    {
        var local = await Card();
        var foreign = await Card("FOREIGN SECRET", root + "-other");
        var history = new Mock<IUnifiedSearchService>();
        history.Setup(s => s.Search(It.IsAny<string>(), It.IsAny<int>())).Returns(new UnifiedSearchResponse("VB-1", 1, 0, []));
        var tool = new SessionSearchTool(history.Object, Recall());
        var result = await tool.SearchHistory($"VB-1, VB-999, {foreign.Key}", 1, Ct);
        Assert.Contains(local.Title, result);
        Assert.Contains("[exact card - project " + root, result);
        Assert.Contains("No card matches VB-999", result);
        Assert.Contains(foreign.Title, result);
        Assert.Contains("WARNING: another repository", result);
        embedder.Verify(e => e.GenerateEmbedding(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BareCardNumberRequiresLaunchingBoardContext()
    {
        var card = await Card();
        Assert.False((await Recall().SearchAsync("card 1", 5, Ct)).Exact);
        var session = Guid.NewGuid().ToString();
        await store.LinkSessionAsync(root, card.Id, session, null, "base:codex", "codex", "Work", "launch", Ct);
        projects.SetupGet(p => p.CurrentSessionId).Returns(session);
        var result = await Recall().SearchAsync("card 1", 5, Ct);
        Assert.True(result.Exact);
        Assert.Contains(card.Key, result.Text);
        Assert.Contains(session, result.SessionIds);
    }

    [Theory]
    [InlineData("GPT-5")]
    [InlineData("UTF-8")]
    [InlineData("dotnet-10")]
    public async Task IncidentalIdentifiersDoNotSuppressUnnamedDiscovery(string identifier)
    {
        var card = await Card(identifier + " proxy startup");
        await store.CreateBoardAsync(root + "-other", "Foreign", identifier[..identifier.IndexOf('-')], Ct);
        var result = await Recall().SearchAsync($"where did we fix {identifier} proxy startup", 1, Ct);
        Assert.False(result.Exact);
        Assert.Contains(card.Title, result.Text);
        Assert.DoesNotContain("No card matches", result.Text);
    }

    [Fact]
    public async Task CurrentAndHistoricalProjectPrefixesStillReportMissingKeys()
    {
        var card = await Card();
        await store.RenameBoardAsync(root, card.BoardId, null, "OLD", Ct);
        var old = await Card("Old display prefix");
        await store.RenameBoardAsync(root, card.BoardId, null, "NEW", Ct);
        var result = await Recall().SearchAsync("OLD-999, NEW-999, VB-999", 1, Ct);
        Assert.True(result.Exact);
        Assert.Contains("No card matches OLD-999", result.Text);
        Assert.Contains("No card matches NEW-999", result.Text);
        Assert.Contains("No card matches VB-999", result.Text);
        Assert.Contains(old.Title, (await Recall().SearchAsync(old.DisplayId, 1, Ct)).Text);
    }

    [Fact]
    public async Task HandoffPreservesDescriptionHistoryAndRecordsProvenanceAndMissingFiles()
    {
        var card = await Card();
        var before = await store.GetCardHistoryAsync(root, card.Id, Ct);
        var author = BoardAuthor.Agent("Codex", "codex", Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(Path.Combine(root, "entry.cs"), "// entry", Ct);
        var handoff = BoardHandoffService.Validate(new("Fixed overflow", "Contain layout", "Tests passed", "None",
            [new("entry.cs", "Editor entry", Symbol: "Open"), new("old.cs", "Historical test", "test")]));
        var saved = await store.SaveHandoffAsync(root, card.Id, handoff, author, Ct);
        Assert.Equal(author, saved!.Author);
        Assert.NotNull(saved.CreatedUtc);
        var detail = (await store.GetCardDetailAsync(root, card.Id, Ct))!;
        Assert.Equal(card.Description, detail.Card.Description);
        Assert.Equal(before.Count, (await store.GetCardHistoryAsync(root, card.Id, Ct)).Count(h => h.Kind != "comment"));
        Assert.Contains("entry.cs", Assert.Single(detail.Comments).Body);
        var shown = BoardHandoffService.WithFileStatus(detail.PreviousWork, root)!;
        Assert.Contains("verify", shown.Files[0].Status);
        Assert.Equal("missing or renamed", shown.Files[1].Status);
        Assert.Null(await store.GetHandoffAsync(root + "-other", card.Id, Ct));
        await store.SaveHandoffAsync(root, card.Id, handoff with { Outcome = "Follow-up" }, author, Ct);
        Assert.Equal("Follow-up", (await store.GetHandoffAsync(root, card.Id, Ct))!.Outcome);
        Assert.Equal(2, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);
        // An older writer updates the card without knowing about the additive tables.
        await store.UpdateCardAsync(root, card.Id, new(Title: "New title"), Ct);
        Assert.Equal("Follow-up", (await store.GetHandoffAsync(root, card.Id, Ct))!.Outcome);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/secret")]
    [InlineData("/etc/passwd")]
    [InlineData("dir/../../secret")]
    public void FileReferencesRejectTraversal(string path) => Assert.Throws<BoardValidationException>(() =>
        BoardHandoffService.Validate(new("Done", "", "", "", [new(path, "Reason")])));

    [Fact]
    public async Task DiscoveryFindsCardTextAndHandoffsRefreshesVectorsAndToleratesMissingModel()
    {
        var relevant = await Card();
        await Card("Deployment credentials");
        var result = await Recall().SearchAsync("where did we fix description overflow", 1, Ct);
        Assert.Contains(relevant.Key, result.Text);
        Assert.Contains("card - BGE/keyword ranking", result.Text);
        var docs = await store.GetSearchDocumentsAsync(0, Ct);
        Assert.All(docs.SelectMany(document => document.Passages), passage => Assert.NotNull(passage.Embedding));
        await store.SaveHandoffAsync(root, relevant.Id, new("Overflow is fixed", "Containment", "Browser checked", "", []), BoardAuthor.User(), Ct);
        Assert.Contains((await store.GetSearchDocumentsAsync(0, Ct)).Single(d => d.Id == relevant.Id).Sources, p => p.Text.Contains("Containment"));
        embedder.Setup(e => e.GenerateEmbedding(It.IsAny<string>())).Throws(new IOException("missing model"));
        Assert.Contains(relevant.Key, (await Recall().SearchAsync("containment", 1, Ct)).Text);
        await store.DeleteCardAsync(root, relevant.Id, Ct);
        Assert.DoesNotContain(relevant.Key, (await Recall().SearchAsync("containment", 1, Ct)).Text);
    }

    [Fact]
    public async Task McpHandoffRoundTripAndDescriptionPagingAreBounded()
    {
        var card = await Card();
        await store.UpdateCardAsync(root, card.Id, new(Description: new string('a', 12000) + "SECOND PAGE"), Ct);
        var service = new BoardService(store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe());
        var tool = new BoardTool(service, projects.Object, store, new BoardWorkflowService(store, new BoardReviewService(store, service)));
        Assert.StartsWith("Saved previous work", await tool.SaveBoardHandoff(new("Fixed", "", "Checked", "", [new("entry.cs", "Start here")]), card.Key, Ct));
        var first = await tool.GetBoardCard(card.Key, cancellationToken: Ct);
        Assert.Contains("descriptionOffset=12000", first);
        Assert.Contains("Previous work", first);
        Assert.Contains("Start here", first);
        Assert.DoesNotContain("SECOND PAGE", first);
        Assert.Contains("SECOND PAGE", await tool.GetBoardCard(card.Key, cancellationToken: Ct, descriptionOffset: 12000));
    }

    [Fact]
    public async Task CommitCandidatesComeFromDurableSnapshotNamesWithoutFileContents()
    {
        var card = await Card();
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        await store.AddCommitAsync(root, card.Id, sha, "Author", "Changed editor", DateTime.UtcNow,
            new SandboxDiffResponse([new SandboxDiffFileResponse("editor.cs", "csharp", "secret before", "secret after")], 1), Ct);
        var candidate = Assert.Single(await store.GetHandoffCandidatesAsync(root, card.Id, Ct));
        Assert.Equal("editor.cs", candidate.Path);
        Assert.Equal(sha, candidate.Commit);
        Assert.Equal("candidate", candidate.Role);
        Assert.Empty(await store.GetHandoffCandidatesAsync(root + "-other", card.Id, Ct));
        var found = await Recall().SearchAsync("VB-1", 1, Ct);
        Assert.Contains("editor.cs", found.Text);
        Assert.DoesNotContain("secret before", found.Text);
    }

    [Fact]
    public async Task DiscussionBudgetDoesNotGrowWithTheCardAndRejectsOversizedInputs()
    {
        var card = (await Card()) with { Description = new string('d', 100000) };
        var prompt = BoardPromptComposer.Compose(card, "Done", "codex", new string('e', 2000), intent: "chat", question: new string('q', 1000));
        Assert.True(prompt.Length < 5000);
        Assert.DoesNotContain(new string('d', 100), prompt);
        Assert.Throws<BoardValidationException>(() => BoardPromptComposer.Compose(card, "Done", "codex", new string('e', 2001), intent: "chat"));
        Assert.Throws<BoardValidationException>(() => BoardPromptComposer.Compose(card, "Done", "codex", null, intent: "chat", question: new string('q', 1001)));
    }

    [Fact]
    public async Task LinkedSessionReadsAreScopedPagedAndIndependentOfBroadHistoryRanking()
    {
        var card = await Card();
        var session = Guid.NewGuid().ToString();
        await store.LinkSessionAsync(root, card.Id, session, null, "base:codex", "codex", "Original implementation", "launch", Ct);
        var history = new Mock<IBertSearchDbService>(MockBehavior.Strict);
        history.Setup(h => h.GetSessionRecallPage(session, 0, 2)).Returns([new(session + ":1", "Original implementation discussion", null)]);
        var recall = Recall(history: history.Object);
        Assert.Contains("Original implementation discussion", (await recall.SearchAsync("VB-1", 1, Ct)).Text);
        var service = new BoardService(store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe());
        var tool = new BoardTool(service, projects.Object, store, new BoardWorkflowService(store, new BoardReviewService(store, service)), history: history.Object);
        Assert.StartsWith("FAIL: session is not linked", await tool.ReadBoardSession("foreign", card.Key, cancellationToken: Ct));
        Assert.StartsWith("FAIL: document is not", await tool.ReadBoardSession(session, card.Key, "foreign:1", cancellationToken: Ct));
        history.Setup(h => h.GetSessionRecallPage(session, 0, 11)).Returns(Enumerable.Range(1, 11).Select(i => new BertStoredDocument(session + ":" + i, "preview", null)).ToList());
        Assert.Contains("offset=10", await tool.ReadBoardSession(session, card.Key, cancellationToken: Ct));
        history.Setup(h => h.GetCapture(session + ":1")).Returns(new BertStoredDocument(session + ":1", new string('a', 12000) + "tail", null));
        Assert.Contains("offset=12000", await tool.ReadBoardSession(session, card.Key, session + ":1", cancellationToken: Ct));
        Assert.Contains("tail", await tool.ReadBoardSession(session, card.Key, session + ":1", 12000, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRecallSurvivesModelConstructionFailure(bool invalidModelConfiguration)
    {
        var card = await Card();
        Exception failure = invalidModelConfiguration
            ? new ArgumentException("invalid model configuration")
            : new IOException("model not installed");
        var recall = Recall(() => throw failure);
        var search = new SessionSearchTool(() => throw failure, recall);
        Assert.Contains(card.Title, await search.SearchHistory("VB-1", 1, Ct));
        Assert.Contains(card.Title, await search.SearchHistory("description overflow", 1, Ct));
    }

    [Fact]
    public async Task MergeRetainsBothHandoffHistoriesAndFullDiscussionReceipts()
    {
        var source = await Card("Source");
        var target = await Card("Target");
        await store.SaveHandoffAsync(root, source.Id, new("Source work", "", "", "", [new("source.cs", "Entry")]), BoardAuthor.User(), Ct);
        await store.SaveHandoffAsync(root, target.Id, new("Target work", "", "", "", []), BoardAuthor.User(), Ct);
        await store.MergeCardsAsync(root, source.Id, target.Id, Ct);
        var detail = (await store.GetCardDetailAsync(root, target.Id, Ct))!;
        Assert.Contains(detail.Comments, c => c.Body.Contains("Source work") && c.Body.Contains("source.cs"));
        Assert.Contains(detail.Comments, c => c.Body.Contains("Target work"));
        Assert.NotNull(detail.PreviousWork);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
