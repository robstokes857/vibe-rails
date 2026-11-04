using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.DB;

public sealed class BoardCodeQualityIgnoresTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-ignores-" + Guid.NewGuid().ToString("N"));
    private readonly string state;
    private readonly BoardStore boards;
    private readonly CodeAnalyzerIgnoreStore legacy;
    private readonly BoardCodeAnalyzerIgnoreStore current;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCodeQualityIgnoresTests()
    {
        Directory.CreateDirectory(root);
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        boards = new($"Data Source={Path.Combine(root, "board.db")};Pooling=False", state);
        legacy = new(state);
        current = new(boards);
    }

    [Fact]
    public async Task NewWritesStayInBoardAndKeepAuthorReasonsAndRemovalHistory()
    {
        var repo = Path.Combine(root, "new");
        await current.UpsertAsync(repo, new("Tests", "directory", "test", "Generated fixtures", DateTime.UtcNow, "Rob"), Ct);
        var first = Assert.Single(await current.ListAsync(repo, Ct));
        Assert.Equal("Rob", first.Author);
        Assert.Equal("Generated fixtures", first.ReasonText);
        await current.UpsertAsync(repo, first with { ReasonText = "Reviewed fixtures", Author = "Reviewer", CreatedUtc = first.CreatedUtc.AddDays(1) }, Ct);
        Assert.Equal(first.CreatedUtc, Assert.Single(await current.ListAsync(repo, Ct)).CreatedUtc);
        Assert.True(await current.RemoveAsync(repo, "Tests", Ct));
        Assert.Empty(await current.ListAsync(repo, Ct));
        var items = await Backup();
        var history = Rows(items, "BoardCodeQualityIgnoreHistory");
        Assert.Equal(3, history.Count);
        Assert.Contains(history, row => row.GetProperty("Author").GetString() == "Rob"
            && row.GetProperty("EntryJson").GetString()!.Contains("Generated fixtures"));
        Assert.Contains(history, row => row.GetProperty("Operation").GetString() == "removed"
            && row.GetProperty("Author").GetString() == Environment.UserName);
        await using var db = new SqliteConnection(state);
        await db.OpenAsync(Ct);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name='CodeAnalyzerIgnores';";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task BackupAdoptsAllLegacyRepositoriesWithoutAScanAndNeverChangesLegacyRows()
    {
        var firstRepo = Path.Combine(root, "first");
        var secondRepo = Path.Combine(root, "second");
        var original = new CodeAnalyzerIgnoredFile("src/legacy.cs", "file", "other", "Older reason", DateTime.UtcNow);
        await legacy.UpsertAsync(firstRepo, original, Ct);
        await legacy.UpsertAsync(secondRepo, original with { Path = "other.cs" }, Ct);
        var first = await Backup();
        Assert.Equal(2, Rows(first, "BoardCodeQualityIgnores").Count);
        Assert.Equal(2, Rows(first, "BoardCodeQualityIgnoreHistory").Count);
        Assert.All(Rows(first, "BoardCodeQualityIgnoreHistory"), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("Author").ValueKind));
        var second = await Backup();
        Assert.Equal(2, Rows(second, "BoardCodeQualityIgnoreHistory").Count);
        Assert.Equal(original, Assert.Single(await legacy.ListAsync(firstRepo, Ct)));
        Assert.Equal(original, Assert.Single(await current.ListAsync(firstRepo, Ct)));
    }

    [Fact]
    public async Task OldWritersRemainReadableAndConflictsCannotResurrectLocalRemoval()
    {
        var repo = Path.Combine(root, "legacy");
        var file = new CodeAnalyzerIgnoredFile("a.cs", "file", "test", "Initial", DateTime.UtcNow);
        await legacy.UpsertAsync(repo, file, Ct);
        Assert.Single(await current.ListAsync(repo, Ct));
        await legacy.UpsertAsync(repo, file with { ReasonText = "Old writer edit" }, Ct);
        Assert.Equal("Old writer edit", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);
        Assert.True(await legacy.RemoveAsync(repo, file.Path, Ct));
        Assert.Empty(await current.ListAsync(repo, Ct));
        await legacy.UpsertAsync(repo, file, Ct);
        Assert.Single(await current.ListAsync(repo, Ct));
        await current.RemoveAsync(repo, file.Path, Ct);
        await legacy.UpsertAsync(repo, file with { ReasonText = "Conflicting old writer edit" }, Ct);
        Assert.Empty(await current.ListAsync(repo, Ct));
        var history = Rows(await Backup(), "BoardCodeQualityIgnoreHistory");
        Assert.Contains(history, row => row.GetProperty("Operation").GetString() == "legacy removed");
        Assert.Contains(history, row => row.GetProperty("EntryJson").GetString()!.Contains("Conflicting old writer edit"));
    }

    [Fact]
    public async Task ProductionRegistrationUsesBoardStoreAndDefaultsAuthorToLocalAccount()
    {
        using var services = new ServiceCollection().AddSqliteStorage(_ => new(Path.Combine(root, "registered-state.db"))).BuildServiceProvider();
        var store = services.GetRequiredService<ICodeAnalyzerIgnoreStore>();
        Assert.IsType<BoardCodeAnalyzerIgnoreStore>(store);
        await store.UpsertManyAsync(root, [new("a.cs", "file", "test", null, DateTime.UtcNow),
            new("b.cs", "file", "config", null, DateTime.UtcNow)], Ct);
        Assert.All(await store.ListAsync(root, Ct), file => Assert.Equal(Environment.UserName, file.Author));
    }

    private async Task<List<BoardDataBackupItem>> Backup()
    {
        var items = new List<BoardDataBackupItem>();
        await boards.ReadDataBackupAsync((item, _) => { items.Add(item); return Task.CompletedTask; }, Ct);
        return items;
    }

    private static List<JsonElement> Rows(List<BoardDataBackupItem> items, string table) => items
        .Where(item => item.Json is not null).Select(item =>
        {
            using var document = JsonDocument.Parse(item.Json!);
            return document.RootElement.Clone();
        }).Where(row => row.GetProperty("table").GetString() == table).Select(row => row.GetProperty("values")).ToList();

    public void Dispose()
    {
        foreach (var file in new[] { "board.db", "registered-state.db" })
        {
            using var connection = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, file), Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
        Directory.Delete(root, true);
    }
}
