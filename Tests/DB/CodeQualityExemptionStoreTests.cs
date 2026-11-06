using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.DB;

public sealed class CodeQualityExemptionStoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-exemptions-" + Guid.NewGuid().ToString("N"));
    private BoardStore store = null!;
    private BoardCardRecord first = null!, second = null!;
    private static readonly BoardAuthor Author = BoardAuthor.Agent("Codex", "codex", "test-session");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        store = OpenStore();
        await store.EnsureDefaultColumnsAsync(root, Ct);
        first = await store.CreateCardAsync(root, new(null, "First", "", null, "medium", null, [], false), Ct);
        second = await store.CreateCardAsync(root, new(null, "Second", "", null, "medium", null, [], false), Ct);
    }

    private BoardStore OpenStore() => new($"Data Source={Path.Combine(root, "board.db")};Pooling=False",
        $"Data Source={Path.Combine(root, "state.db")};Pooling=False");

    [Fact]
    public async Task ScopesAreIndependentAndPersistAcrossRestart()
    {
        var temporary = await store.SaveCodeQualityExemptionAsync(root, first.Id, "./src\\fixture.cs", false, "Test fixture", Author, Ct);
        var permanent = await store.SaveCodeQualityExemptionAsync(root, first.Id, "generated.cs", true, "Generated source", Author, Ct);
        Assert.Equal("src/fixture.cs", temporary.Path);
        Assert.Equal(first.Id, temporary.CardId);
        Assert.Null(permanent.CardId);
        var reopened = OpenStore();
        Assert.Equal(2, (await reopened.GetCodeQualityExemptionsAsync(root, first.Id, Ct)).Count);
        Assert.Equal(permanent, Assert.Single(await reopened.GetCodeQualityExemptionsAsync(root, second.Id, Ct)));
        Assert.Equal(permanent, Assert.Single(await reopened.GetCodeQualityExemptionsAsync(root, cancellationToken: Ct)));
        Assert.Empty(await reopened.GetCodeQualityExemptionsAsync(root + "-other", first.Id, Ct));
        Assert.False(await reopened.RevokeCodeQualityExemptionAsync(root, second.Id, temporary.Id, Author, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => reopened.RevokeCodeQualityExemptionAsync(root + "-other", first.Id, permanent.Id, Author, Ct));
        Assert.True(await reopened.RevokeCodeQualityExemptionAsync(root, second.Id, permanent.Id, Author, Ct));
    }

    [Fact]
    public async Task ReplacementAndRevocationKeepAttributedHistoryInBackup()
    {
        var original = await store.SaveCodeQualityExemptionAsync(root, first.Id, "a.cs", false, "Original reason", Author, Ct);
        var reviewer = BoardAuthor.Agent("Reviewer", "claude", "review-session");
        var replacement = await store.SaveCodeQualityExemptionAsync(root, first.Id, "a.cs", false, "Reviewed reason", reviewer, Ct);
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(replacement, Assert.Single(await store.GetCodeQualityExemptionsAsync(root, first.Id, Ct)));
        Assert.True(await store.RevokeCodeQualityExemptionAsync(root, first.Id, replacement.Id, Author, Ct));
        Assert.Empty(await store.GetCodeQualityExemptionsAsync(root, first.Id, Ct));
        var rows = new List<JsonElement>();
        await store.ReadDataBackupAsync((item, _) =>
        {
            if (item.Json is not null)
            {
                using var doc = JsonDocument.Parse(item.Json);
                if (doc.RootElement.GetProperty("table").GetString() == "BoardCodeQualityExemptions")
                    rows.Add(doc.RootElement.GetProperty("values").Clone());
            }
            return Task.CompletedTask;
        }, Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotEqual(JsonValueKind.Null, row.GetProperty("RevokedUTC").ValueKind));
        Assert.Contains(rows, row => row.GetProperty("EntryJson").GetString()!.Contains("Original reason")
            && row.GetProperty("RevokedByJson").GetString()!.Contains("Reviewer"));
        Assert.Contains(rows, row => row.GetProperty("EntryJson").GetString()!.Contains("review-session")
            && row.GetProperty("RevokedByJson").GetString()!.Contains("Codex"));
    }

    [Theory]
    [InlineData("../a.cs", "Reason")]
    [InlineData("/a.cs", "Reason")]
    [InlineData("C:\\a.cs", "Reason")]
    [InlineData("src/*.cs", "Reason")]
    [InlineData("src/", "Reason")]
    [InlineData("src//a.cs", "Reason")]
    [InlineData("a.cs", " ")]
    public async Task InvalidInputsNeverWrite(string path, string reason)
    {
        await Assert.ThrowsAsync<BoardValidationException>(() => store.SaveCodeQualityExemptionAsync(root, first.Id, path, false, reason, Author, Ct));
        Assert.Empty(await store.GetCodeQualityExemptionsAsync(root, first.Id, Ct));
    }

    [Fact]
    public async Task DeletingACardStopsItsExemptionsWithoutDeletingTheirHistory()
    {
        await store.SaveCodeQualityExemptionAsync(root, first.Id, "a.cs", false, "Keep history", Author, Ct);
        Assert.True(await store.DeleteCardAsync(root, first.Id, Ct));
        Assert.Empty(await store.GetCodeQualityExemptionsAsync(root, first.Id, Ct));
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BoardCodeQualityExemptions;";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task ProjectCaseUsesTheHostsPathSemantics()
    {
        Assert.SkipUnless(BoardPaths.ProjectPathComparison == StringComparison.OrdinalIgnoreCase, "Case-insensitive host only.");
        await store.SaveCodeQualityExemptionAsync(root, first.Id, "a.cs", true, "First", Author, Ct);
        var replacement = await store.SaveCodeQualityExemptionAsync(root.ToUpperInvariant(), first.Id, "a.cs", true, "Second", Author, Ct);
        Assert.Equal(replacement, Assert.Single(await store.GetCodeQualityExemptionsAsync(root, first.Id, Ct)));
    }

    [Fact]
    public async Task WrongProjectCannotWriteAndOldIgnoreReadersNeverSeeCardExemptions()
    {
        await Assert.ThrowsAsync<BoardValidationException>(() => store.SaveCodeQualityExemptionAsync(root + "-other", first.Id, "a.cs", false, "Fixture", Author, Ct));
        await store.SaveCodeQualityExemptionAsync(root, first.Id, "a.cs", false, "Fixture", Author, Ct);
        Assert.Empty(await store.GetCodeQualityIgnoresAsync(root, Ct));
        Assert.Empty(await new BoardCodeAnalyzerIgnoreStore(OpenStore()).ListAsync(root, Ct));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(root, "board.db")}"));
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(root, "state.db")}"));
        Directory.Delete(root, true);
        return ValueTask.CompletedTask;
    }
}
