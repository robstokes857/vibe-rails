using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

/// <summary>
/// Pins the directory-aware ignore matcher: file rules match exactly, directory rules
/// match the directory itself plus everything underneath (but not siblings), and the
/// store round-trips MatchKind/ReasonKind/ReasonText through SQLite.
/// </summary>
public sealed class CodeAnalyzerIgnoreStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-codeanalyzer-ignore-test-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public CodeAnalyzerIgnoreStoreTests()
    {
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate";
    }

    public void Dispose()
    {
        // Release pooled SQLite connections before deleting: without this the file handle survives,
        // File.Delete throws, and the swallowed exception leaks a temp .db per test on Windows.
        // Scoped to this class's connection string: a process-wide ClearAllPools() disposes handles
        // out from under DB test classes running in parallel.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS temp dir owns the leftovers.
        }
    }

    [Fact]
    public async Task UpsertAsync_FileRule_MatchesExactPathOnly()
    {
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests/Foo.cs", CodeAnalyzerIgnoreMatchKind.File, "test", null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);

        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Foo.cs", rules));
        // Sibling file with a similar name must NOT match (exact path equality only).
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("Tests/FooBar.cs", rules));
        // A file underneath a "Tests/Foo" directory would NOT match either — this is a
        // file rule, not a directory rule. Use MatchKind=directory for that.
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Foo/Sub.cs", rules));
    }

    [Fact]
    public async Task UpsertAsync_DirectoryRule_MatchesPathAndEverythingUnderneath()
    {
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests/Services", CodeAnalyzerIgnoreMatchKind.Directory, "test", "test scaffolding", DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);

        // The directory path itself (if it ever appears as a file).
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Services", rules));
        // A file directly underneath.
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Services/Jobs/JobRunExecutorTests.cs", rules));
        // A file in a subdirectory.
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Services/Sub/Helper.cs", rules));
        // Critical: a sibling whose name shares a prefix must NOT match.
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("Tests/ServicesExtra.cs", rules));
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("Tests/ServicesJunction/Sibling.cs", rules));
        // A file outside the directory entirely.
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("src/Program.cs", rules));
    }

    [Fact]
    public async Task IsIgnored_FollowsHostFilesystemCaseSemantics()
    {
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests/Foo.cs", CodeAnalyzerIgnoreMatchKind.File, null, null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Src/Payments", CodeAnalyzerIgnoreMatchKind.Directory, null, null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);

        // Exact-case matches always hold.
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Foo.cs", rules));
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Src/Payments/Processor.cs", rules));

        // Case-variant matches follow the host filesystem: case-insensitive on Windows/macOS,
        // case-sensitive on Linux (where Foo.cs and foo.cs are genuinely different files).
        var caseInsensitiveHost = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(caseInsensitiveHost, CodeAnalyzerIgnoreStore.IsIgnored("tests/foo.cs", rules));
        Assert.Equal(caseInsensitiveHost, CodeAnalyzerIgnoreStore.IsIgnored("SRC/payments/Processor.cs", rules));
    }

    [Fact]
    public async Task IsIgnored_ReturnsFalse_WhenNoRulesExist()
    {
        Assert.False(CodeAnalyzerIgnoreStore.IsIgnored("anything.cs", []));
    }

    [Fact]
    public async Task RemoveAsync_DeletesByPath_RegardlessOfMatchKind()
    {
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests/Foo", CodeAnalyzerIgnoreMatchKind.Directory, "test", null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var removed = await store.RemoveAsync(repo, "Tests/Foo", TestContext.Current.CancellationToken);
        Assert.True(removed);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesMatchKindAndReason_WhenPathAlreadyExists()
    {
        // The PK is (RepositoryPath, Path), so upserting the same path with a different
        // MatchKind replaces the row — promoting a file rule to a directory rule (or vice
        // versa) without leaving a stale entry behind.
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        var created = DateTime.UtcNow;
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests", CodeAnalyzerIgnoreMatchKind.File, null, null, created),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("Tests", CodeAnalyzerIgnoreMatchKind.Directory, "test", "all of Tests/", created),
            TestContext.Current.CancellationToken);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);
        var rule = Assert.Single(rules);
        Assert.Equal(CodeAnalyzerIgnoreMatchKind.Directory, rule.MatchKind);
        Assert.Equal("test", rule.ReasonKind);
        Assert.Equal("all of Tests/", rule.ReasonText);
    }

    [Fact]
    public async Task NormalizePath_StripsLeadingDotSlash_AndTrailingSlash()
    {
        // Round-trip: store "./Tests/Foo/" and confirm it matches "Tests/Foo" and
        // "Tests/Foo/Bar.cs" (when stored as a directory rule).
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        var repo = "C:/repo";
        await store.UpsertAsync(repo,
            new CodeAnalyzerIgnoredFile("./Tests/Foo/", CodeAnalyzerIgnoreMatchKind.Directory, null, null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var rules = await store.GetIgnoreRulesAsync(repo, TestContext.Current.CancellationToken);
        var rule = Assert.Single(rules);
        Assert.Equal("Tests/Foo", rule.Path);
        Assert.True(CodeAnalyzerIgnoreStore.IsIgnored("Tests/Foo/Bar.cs", rules));
    }

    [Fact]
    public async Task RulesAreScopedPerRepository()
    {
        var store = new CodeAnalyzerIgnoreStore(_connectionString);
        await store.UpsertAsync("C:/repo-a",
            new CodeAnalyzerIgnoredFile("Tests/Foo.cs", CodeAnalyzerIgnoreMatchKind.File, null, null, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        var rulesB = await store.GetIgnoreRulesAsync("C:/repo-b", TestContext.Current.CancellationToken);
        Assert.Empty(rulesB);

        var rulesA = await store.GetIgnoreRulesAsync("C:/repo-a", TestContext.Current.CancellationToken);
        Assert.Single(rulesA);
    }
}
