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
    private readonly ManualClock clock = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCodeQualityIgnoresTests()
    {
        Directory.CreateDirectory(root);
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        boards = new($"Data Source={Path.Combine(root, "board.db")};Pooling=False", state) { LegacyIgnoreClock = clock };
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
        await OldWriter(() => legacy.UpsertAsync(repo, file with { ReasonText = "Old writer edit" }, Ct));
        Assert.Equal("Old writer edit", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);
        await OldWriter(async () => Assert.True(await legacy.RemoveAsync(repo, file.Path, Ct)));
        Assert.Empty(await current.ListAsync(repo, Ct));
        await OldWriter(() => legacy.UpsertAsync(repo, file, Ct));
        Assert.Single(await current.ListAsync(repo, Ct));
        await current.RemoveAsync(repo, file.Path, Ct);
        await OldWriter(() => legacy.UpsertAsync(repo, file with { ReasonText = "Conflicting old writer edit" }, Ct));
        Assert.Empty(await current.ListAsync(repo, Ct));
        var history = Rows(await Backup(), "BoardCodeQualityIgnoreHistory");
        Assert.Contains(history, row => row.GetProperty("Operation").GetString() == "legacy removed");
        Assert.Contains(history, row => row.GetProperty("EntryJson").GetString()!.Contains("Conflicting old writer edit"));
    }

    /// <summary>
    /// VIBE-284 item 4: every Rules scan, Code quality check and pre-commit MintLint step reads the
    /// list. Once the old side is adopted, a read must not open a board.db write transaction, so it
    /// neither waits behind nor blocks a concurrent Board write. A later old-side edit still lands
    /// once the observation window (VIBE-276) has passed.
    /// </summary>
    [Fact]
    public async Task AdoptedLegacyRulesAreReadWithoutTheBoardWriteLock()
    {
        var repo = Path.Combine(root, "steady");
        var file = new CodeAnalyzerIgnoredFile("a.cs", "file", "test", "Initial", DateTime.UtcNow);
        await legacy.UpsertAsync(repo, file, Ct);
        Assert.Single(await current.ListAsync(repo, Ct));
        await current.UpsertAsync(repo, new("local.cs", "file", "test", "Local choice", DateTime.UtcNow, "Rob"), Ct);
        Assert.Equal(2, (await current.ListAsync(repo, Ct)).Count);

        await using (var writer = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")};Pooling=False"))
        {
            await writer.OpenAsync(Ct);
            using var held = writer.BeginTransaction();
            var read = await current.ListAsync(repo, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(["a.cs", "local.cs"], read.Select(rule => rule.Path).Order());
        }

        await OldWriter(() => legacy.UpsertAsync(repo, file with { ReasonText = "Old writer edit" }, Ct));
        Assert.Equal("Old writer edit", (await current.ListAsync(repo, Ct)).Single(rule => rule.Path == "a.cs").ReasonText);
        Assert.Equal(3, Rows(await Backup(), "BoardCodeQualityIgnoreHistory").Count);
    }

    /// <summary>
    /// VIBE-276: the list lives in board.db; state.db is read only to adopt an older version's
    /// edits, at most once per repository per window. Inside the window an old-side edit waits;
    /// after it, or in another repository, it lands. A backup always reads the old side.
    /// </summary>
    [Fact]
    public async Task LegacyRowsAreReadAtMostOncePerWindowPerRepository()
    {
        var repo = Path.Combine(root, "throttled");
        var other = Path.Combine(root, "other");
        var file = new CodeAnalyzerIgnoredFile("a.cs", "file", "test", "Initial", DateTime.UtcNow);
        await legacy.UpsertAsync(repo, file, Ct);
        Assert.Equal("Initial", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);

        await legacy.UpsertAsync(repo, file with { ReasonText = "Old writer edit" }, Ct);
        await legacy.UpsertAsync(other, file, Ct);
        clock.Advance(BoardStore.LegacyIgnoreObservationInterval - TimeSpan.FromSeconds(1));
        Assert.Equal("Initial", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);
        Assert.Single(await current.ListAsync(other, Ct));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Old writer edit", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);

        await legacy.UpsertAsync(repo, file with { ReasonText = "Edit before a backup" }, Ct);
        Assert.Contains(Rows(await Backup(), "BoardCodeQualityIgnoreHistory"),
            row => row.GetProperty("EntryJson").GetString()!.Contains("Edit before a backup"));
        Assert.Equal("Edit before a backup", Assert.Single(await current.ListAsync(repo, Ct)).ReasonText);
    }

    /// <summary>
    /// VIBE-276: inside the window, reads, saves and removals never open state.db. An unreadable
    /// state.db proves it: the window's reads succeed, and the first read after it fails visibly
    /// (an unreadable list never silently ignores files) and is retried on the next call.
    /// </summary>
    [Fact]
    public async Task ReadsAndWritesInsideTheWindowDoNotOpenStateDb()
    {
        var repo = Path.Combine(root, "no-state");
        await legacy.UpsertAsync(repo, new("legacy.cs", "file", "test", "Old", DateTime.UtcNow), Ct);
        Assert.Single(await current.ListAsync(repo, Ct));

        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(root, "state.db"), "not a database", Ct);
        await current.UpsertAsync(repo, new("local.cs", "file", "test", "Local", DateTime.UtcNow, "Rob"), Ct);
        Assert.True(await current.RemoveAsync(repo, "legacy.cs", Ct));
        Assert.Equal("local.cs", Assert.Single(await current.ListAsync(repo, Ct)).Path);

        clock.Advance(BoardStore.LegacyIgnoreObservationInterval);
        await Assert.ThrowsAnyAsync<SqliteException>(() => current.ListAsync(repo, Ct));
        await Assert.ThrowsAnyAsync<SqliteException>(() => current.ListAsync(repo, Ct));
    }

    /// <summary>An older version edits its own state.db list, then the window passes.</summary>
    private async Task OldWriter(Func<Task> write)
    {
        await write();
        clock.Advance(BoardStore.LegacyIgnoreObservationInterval);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
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
