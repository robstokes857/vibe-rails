using Tests.Services.BertV2;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCrossProjectLinksTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-local-links-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly string project;
    private readonly string otherProject;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCrossProjectLinksTests()
    {
        Directory.CreateDirectory(root);
        var connectionString = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(connectionString, connectionString);
        project = Path.Combine(root, "source");
        otherProject = Path.Combine(root, "other");
    }

    private async Task<BoardCardRecord> Create(string path, string title)
    {
        await store.EnsureDefaultColumnsAsync(path, Ct);
        return await store.CreateCardAsync(path, new(null, title, "", null, "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task ForeignTargetsRequireImmutableIdentity_AndDeletedTargetsStayUnavailable()
    {
        var source = await Create(project, "Source");
        var other = await Create(otherProject, "Other");
        await store.UpdateCardAsync(otherProject, other.Id, new(DisplayId: "FOREIGN-99"), Ct);
        Assert.Null(await store.LinkCardAsync(project, source.Id, "FOREIGN-99", Ct));
        Assert.Null(await store.LinkCardAsync(project, source.Id, BoardKeys.Format(other.KeyPrefix, other.Number), Ct));
        Assert.Equal(other.Id, (await store.LinkCardAsync(project, source.Id, other.Key.ToLowerInvariant(), Ct))!.Id);
        Assert.True(await store.UnlinkCardAsync(project, source.Id, other.Id, Ct));
        await store.DeleteCardAsync(otherProject, other.Id, Ct);
        Assert.Null(await store.LinkCardAsync(project, source.Id, other.Id, Ct));
        Assert.Null(await store.LinkCardAsync(project, source.Id, other.Key, Ct));
        Assert.Empty((await store.GetCardLinkCandidatesAsync(project, source.Id, "Other", Ct))!);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.CreateCardAsync(project,
            new(null, "Draft", "", null, "medium", null, [], false, LinkedCardIds: [other.Id]), Ct));
        Assert.Single(await store.GetCardsAsync(project, Ct));
    }

    [Fact]
    public async Task CandidatesPreferTheCurrentProject_AndResponsesIdentifyOtherProjects()
    {
        var source = await Create(project, "Source");
        var nearby = await Create(project, "Matching work");
        var other = await Create(otherProject, "Matching work");
        var service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        var candidates = (await service.GetCardLinkCandidatesAsync(project, source.Id, "Matching", Ct))!;
        Assert.Equal([nearby.Id, other.Id], candidates.Cards.Select(card => card.Id));
        Assert.True(candidates.Cards[0].IsCurrentProject);
        Assert.False(candidates.Cards[1].IsCurrentProject);
        Assert.Equal(otherProject, candidates.Cards[1].ProjectPath);
        var linked = (await service.LinkCardAsync(project, source.Id, other.Key, Ct))!;
        Assert.False(linked.IsCurrentProject);
        var detail = (await service.GetCardAsync(project, source.Id, Ct))!;
        Assert.Equal(linked, Assert.Single(detail.LinkedCards));
        var json = JsonSerializer.Serialize(detail, AppJsonSerializerContext.Default.BoardCardResponse);
        using var parsed = JsonDocument.Parse(json);
        var saved = parsed.RootElement.GetProperty("linkedCards")[0];
        Assert.Equal(otherProject, saved.GetProperty("projectPath").GetString());
        Assert.False(saved.GetProperty("isCurrentProject").GetBoolean());
    }

    [Fact]
    public async Task ForeignLinksRemainLocal_AndDoNotEnterPublishedActivity()
    {
        var source = await Create(project, "Source");
        var nearby = await Create(project, "Nearby");
        var other = await Create(otherProject, "Private other project title");
        await store.LinkCardAsync(project, source.Id, nearby.Id, Ct);
        await store.LinkCardAsync(project, source.Id, other.Id, Ct);
        Assert.Equal(2, (await store.GetCardDetailAsync(project, source.Id, Ct))!.LinkedCards.Count);
        var published = (await store.GetSyncActivityAsync(project, source.BoardId, source.Id, Ct))!;
        Assert.Equal(nearby.Id, Assert.Single(published.LinkedCards).Id);
        Assert.Empty((await store.GetSyncActivityAsync(otherProject, other.BoardId, other.Id, Ct))!.LinkedCards);
    }

    [Fact]
    public async Task SearchCandidatesUseDiscussion_AndExcludeExistingLinksBeforeTheLimit()
    {
        var source = await Create(project, "Needle source");
        for (var i = 0; i < 51; i++)
        {
            var linked = await Create(project, "Needle already linked " + i);
            await store.LinkCardAsync(project, source.Id, linked.Id, Ct);
        }
        var other = await Create(otherProject, "Unrelated title");
        await store.AddCommentAsync(otherProject, other.Id, BoardAuthor.User(), "The needle is in this discussion.", Ct);
        var embedder = new Mock<IBertV2BgeEmbedder>();
        embedder.Setup(model => model.GenerateEmbedding(It.IsAny<string>())).Throws<InvalidOperationException>();
        var search = new BoardSearchService(SearchTestIndex.Build(store, root), embedder.Object, NullLogger<BoardSearchService>.Instance);
        var service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), searchService: search);
        var found = Assert.Single((await service.GetCardLinkCandidatesAsync(project, source.Id, "needle", Ct))!.Cards);
        Assert.Equal(other.Id, found.Id);
        Assert.False(found.IsCurrentProject);
        Assert.Null(await service.GetCardLinkCandidatesAsync(otherProject, source.Id, "needle", Ct));
        Assert.Empty(await store.GetLinkedCardIdsAsync(otherProject, source.Id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignSourceSearchCanPreferTheOpenWorkspace_WithoutChangingSourceScope(bool useSearchService)
    {
        var local = await Create(project, "Local workspace card");
        var source = await Create(otherProject, "Foreign source");
        var peer = await Create(otherProject, "Foreign peer");
        var search = useSearchService
            ? new BoardSearchService(SearchTestIndex.Build(store, root), Mock.Of<IBertV2BgeEmbedder>(), NullLogger<BoardSearchService>.Instance)
            : null;
        var service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), searchService: search);
        var cards = (await service.GetCardLinkCandidatesAsync(otherProject, source.Id, "", Ct, preferredProjectPath: project))!.Cards;
        Assert.Equal([local.Id, peer.Id], cards.Select(card => card.Id));
        Assert.True(cards[0].IsCurrentProject);
        Assert.False(cards[1].IsCurrentProject);
        Assert.Null(await service.GetCardLinkCandidatesAsync(project, source.Id, "", Ct, preferredProjectPath: otherProject));
    }

    [Fact]
    public async Task MergeKeepsForeignLinks_WithoutExpandingMergeTargetScope()
    {
        var source = await Create(project, "Source");
        var target = await Create(project, "Destination");
        var other = await Create(otherProject, "Related elsewhere");
        await store.LinkCardAsync(project, source.Id, other.Id, Ct);
        Assert.Null(await store.MergeCardsAsync(project, source.Id, other.Id, Ct));
        Assert.NotNull(await store.MergeCardsAsync(project, source.Id, target.Id, Ct));
        Assert.Equal(other.Id, Assert.Single((await store.GetCardDetailAsync(project, target.Id, Ct))!.LinkedCards).Id);
        Assert.Equal(target.Id, Assert.Single((await store.GetCardDetailAsync(otherProject, other.Id, Ct))!.LinkedCards).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectOnlyMergeCandidatesKeepAlreadyLinkedTargetsAndDoNotUseThePreferredProjectAsScope(bool useSearchService)
    {
        var source = await Create(project, "Source");
        var local = await Create(project, "Matching destination");
        var foreign = await Create(otherProject, "Matching foreign");
        await store.LinkCardAsync(project, source.Id, local.Id, Ct);
        var model = new Mock<IBertV2BgeEmbedder>();
        model.Setup(embedder => embedder.GenerateEmbedding(It.IsAny<string>())).Throws<InvalidOperationException>();
        var search = useSearchService ? new BoardSearchService(SearchTestIndex.Build(store, root), model.Object, NullLogger<BoardSearchService>.Instance) : null;
        var service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), searchService: search);
        // Merge uses the unbound candidate search: existing relationships must not hide a destination.
        var scoped = (await service.GetCardLinkCandidatesAsync(project, null, "Matching", Ct,
            preferredProjectPath: otherProject, currentProjectOnly: true))!.Cards;
        Assert.Equal(local.Id, Assert.Single(scoped).Id);
        Assert.Equal(project, scoped[0].ProjectPath);
        Assert.False(scoped[0].IsCurrentProject); // ownership label still follows the preferred workspace
        var global = (await service.GetCardLinkCandidatesAsync(project, null, "Matching", Ct))!.Cards;
        Assert.Contains(global, card => card.Id == foreign.Id);
        // Ordinary existing-card link search keeps excluding its current relationships.
        var linkCandidates = (await service.GetCardLinkCandidatesAsync(project, source.Id, "Matching", Ct))!.Cards;
        Assert.Equal(foreign.Id, Assert.Single(linkCandidates).Id);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}
