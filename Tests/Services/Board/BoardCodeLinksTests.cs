using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCodeLinksTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-code-links-" + Guid.NewGuid().ToString("N"));
    private readonly Lazy<BoardStore> lazyStore;
    private BoardStore store => lazyStore.Value;
    private readonly string connection;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCodeLinksTests()
    {
        Directory.CreateDirectory(root);
        connection = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        lazyStore = new(() =>
        {
            _ = new Repository(connection);
            return new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", connection);
        });
    }

    [Theory]
    [InlineData("https://GitHub.com/Org/Repo/pull/0042#files", "https://github.com/org/repo/pull/42", "pull_request")]
    [InlineData("https://github.com/org/repo/commit/ABC1234/", "https://github.com/org/repo/commit/abc1234", "commit")]
    public void NormalizesReferences(string value, string expected, string kind)
    {
        var link = BoardCodeLinks.Parse(value);
        Assert.Equal(expected, link.Url);
        Assert.Equal(kind, link.Kind);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com.evil/org/repo/pull/1")]
    [InlineData("https://github.com@evil.com/org/repo/pull/1")]
    [InlineData("http://github.com/org/repo/pull/1")]
    [InlineData("https://github.com/org/repo/issues/1")]
    [InlineData("https://github.com/org/repo/pull/0")]
    [InlineData("https://github.com/org/repo/commit/not-a-sha")]
    [InlineData("https://github.com/org/repo/commit/abc1234?token=secret")]
    [InlineData("https://github.com/org/repo/pull/1/evil")]
    public void RejectsNonGitHubReferences(string value)
    {
        Assert.Throws<BoardValidationException>(() => BoardCodeLinks.Parse(value));
    }

    [Fact]
    public async Task AutomaticUpgradePreservesExistingCards()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Older card", "Keep this data", null, "medium", null, [], false), Ct);
        var boardConnection = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        // Disposable fixture models the schema immediately before this feature existed.
        await using (var db = new SqliteConnection(boardConnection))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE BoardCodeLinks; DELETE FROM SchemaMigrations WHERE Component='board-code-links';";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var upgraded = new BoardStore(boardConnection, connection);
        var retained = (await upgraded.GetCardDetailAsync(root, card.Id, Ct))!;
        Assert.Equal("Keep this data", retained.Card.Description);
        Assert.Empty(retained.CodeLinks);
        Assert.NotNull(await upgraded.AddCodeLinkAsync(root, card.Id, "https://github.com/org/repo/pull/42", Ct));
    }

    [Fact]
    public async Task PersistsDeduplicatesScopesAndUnlinksWithoutTouchingCommits()
    {
        var service = new BoardService(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        var card = await service.CreateCardAsync(root, new(Title: "Link work"), Ct);
        var other = await service.CreateCardAsync(root, new(Title: "Other"), Ct);
        var link = (await service.LinkCodeAsync(root, card.Id, "https://github.com/org/repo/pull/42", Ct))!;
        Assert.Equal(link.Id, (await service.LinkCodeAsync(root, card.Id, "https://github.com/ORG/REPO/pull/42#files", Ct))!.Id);
        var reloaded = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", connection);
        Assert.Equal(link, Assert.Single((await reloaded.GetCardDetailAsync(root, card.Id, Ct))!.CodeLinks));
        Assert.Empty((await service.GetCardAsync(root, card.Id, Ct))!.Commits);
        Assert.Equal(link, Assert.Single((await service.GetCardAsync(root, card.Id, Ct))!.CodeLinks));
        Assert.Empty(await reloaded.GetCodeLinksAsync(root + "-other", card.Id, Ct));
        Assert.False(await service.UnlinkCodeAsync(root, other.Id, link.Id, Ct));
        Assert.True(await service.UnlinkCodeAsync(root, card.Id, link.Id, Ct));
        Assert.Empty((await service.GetCardAsync(root, card.Id, Ct))!.CodeLinks);
    }

    [Fact]
    public async Task MergeDeduplicatesLinksAndRetainsSourceRows()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var source = await store.CreateCardAsync(root, new(null, "Source", "", null, "medium", null, [], false), Ct);
        var target = await store.CreateCardAsync(root, new(null, "Target", "", null, "medium", null, [], false), Ct);
        const string url = "https://github.com/org/repo/pull/42";
        await store.AddCodeLinkAsync(root, source.Id, url, Ct);
        await store.AddCodeLinkAsync(root, target.Id, url, Ct);
        await store.AddCodeLinkAsync(root, source.Id, "https://github.com/org/repo/commit/abc1234", Ct);
        await store.MergeCardsAsync(root, source.Id, target.Id, Ct);
        Assert.Equal(2, (await store.GetCodeLinksAsync(root, target.Id, Ct)).Count);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")};Pooling=False");
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BoardCodeLinks WHERE CardId=$source";
        command.Parameters.AddWithValue("$source", source.Id);
        Assert.Equal(2L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task ExportIncludesReferencesFromEveryAttachedCardAndNoUnrelatedCard()
    {
        var repository = new Repository(connection, boardStoreFactory: () => store);
        const string session = "77777777-7777-4777-8777-777777777777";
        await repository.CreateSessionAsync(session, "Codex", null, root, Environment.ProcessId);
        await repository.CompleteSessionAsync(session, 0);
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var cards = new List<BoardCardRecord>();
        for (var index = 0; index < 3; index++)
        {
            var card = await store.CreateCardAsync(root, new(null, "Card " + index, "PRIVATE_DESCRIPTION", null, "medium", null, [], false), Ct);
            cards.Add(card);
            await store.AddCodeLinkAsync(root, card.Id, $"https://github.com/org/repo/pull/{index + 1}", Ct);
            if (index < 2) await store.LinkSessionAsync(root, card.Id, session, null, "base:codex", "codex", "Work", BoardSessionRecord.ManualOrigin, Ct);
        }
        using var output = new MemoryStream();
        Assert.NotNull(await repository.WriteSessionExportAsync(session, output, Ct));
        using var json = JsonDocument.Parse(output.ToArray());
        var links = json.RootElement.GetProperty("codeLinks").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, link => Assert.Equal("pull_request", link.GetProperty("kind").GetString()));
        Assert.DoesNotContain(links, link => link.GetProperty("cardKey").GetString() == cards[2].Key);
        Assert.DoesNotContain("PRIVATE_DESCRIPTION", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }
}
