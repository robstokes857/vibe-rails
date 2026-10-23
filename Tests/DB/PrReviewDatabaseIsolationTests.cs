using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class PrReviewDatabaseIsolationTests : IDisposable
{
    private const string Repo = "octo/widgets";
    private static readonly PrReviewPullRequestFields Fields = new("A PR", "octocat", DateTime.UtcNow);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pr-review-split;" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private string State => Path.Combine(_directory, "state.db");
    private string Reviews => Path.Combine(_directory, "pr_reviews.db");
    private PrReviewStore Legacy => new(Connection(State));
    private IPrReviewStore Current => SqliteStorage.CreatePrReviewStore(State);

    public PrReviewDatabaseIsolationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_and_stdio_hosts_use_the_same_dedicated_database(bool stdioFirst)
    {
        var services = new ServiceCollection();
        if (stdioFirst) services.AddSqlitePrReviewStorage(_ => State);
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(State));
        if (!stdioFirst) services.AddSqlitePrReviewStorage(_ => State);
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IPrReviewStore>().StartReviewAsync(Repo, 1, Fields, "abc", _ct);

        var stdio = new ServiceCollection();
        stdio.AddSqlitePrReviewStorage(_ => State);
        using var second = stdio.BuildServiceProvider();
        Assert.Single(await second.GetRequiredService<IPrReviewStore>().GetReviewsAsync(Repo, 1, _ct));
        Assert.True(File.Exists(Reviews));
        Assert.False(File.Exists(State)); // PR operations do not even create a legacy state file.
        Assert.Equal(1L, Scalar(Reviews, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Adoption_preserves_metadata_passes_drafts_and_raw_timestamps_without_changing_legacy()
    {
        var old = Legacy;
        await old.StartReviewAsync(Repo, 12, Fields, "first", _ct);
        await old.CompleteReviewAsync(Repo, 12, PrReviewVerdict.RequestChanges, true, "blocking feedback", null, "summary", _ct);
        await old.StartReviewAsync(Repo, 12, Fields, "second", _ct);
        await old.SaveDraftAsync(Repo, 12, new(Feedback: "unfinished draft", Verdict: PrReviewVerdict.Comments), null, _ct);
        await old.RecordGitHubStatesAsync(Repo, [new(12, PrGitHubState.Merged, DateTime.UtcNow)], _ct);
        var before = await old.GetAsync(Repo, 12, _ct);
        var passes = await old.GetReviewsAsync(Repo, 12, _ct);
        var schema = Scalar(State, "PRAGMA schema_version;");

        Assert.Equal(before, await Current.GetAsync("OCTO/widgets", 12, _ct));
        var adopted = await Current.GetReviewsAsync(Repo, 12, _ct);
        Assert.Equal(passes.Select(r => r with { Id = 0 }), adopted.Select(r => r with { Id = 0 }));
        for (var i = 0; i < 3; i++) Assert.Equal(2, (await Current.GetReviewsAsync(Repo, 12, _ct)).Count);
        Assert.Equal(before, await old.GetAsync(Repo, 12, _ct));
        Assert.Equal(passes, await old.GetReviewsAsync(Repo, 12, _ct));
        Assert.Equal(schema, Scalar(State, "PRAGMA schema_version;"));
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewLegacyAdoptions;"));
        Assert.Equal(2L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewLegacyIds;"));
        Assert.Null(Scalar(Reviews, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Shipped_state_generation_and_receipt_remain_unchanged_and_older_SQL_keeps_working()
    {
        await Legacy.StartReviewAsync(Repo, 8, Fields, null, _ct);
        using (var db = SqliteConnectionFactory.Open(Connection(State)))
        using (var command = db.CreateCommand())
        {
            // Model the shipped feature: only pr-reviews/1, in the generation-3 state file.
            command.CommandText = """
                DROP TABLE PrReviewLegacyAdoptions; DROP TABLE PrReviewLegacyIds;
                DELETE FROM SchemaMigrations WHERE Component='pr-review-adoption';
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
        }
        var schema = Scalar(State, "PRAGMA schema_version;");
        var receipts = Scalar(State, "SELECT COUNT(*) FROM SchemaMigrations;");
        Assert.NotNull(await Current.GetAsync(Repo, 8, _ct));
        using (var db = SqliteConnectionFactory.Open(Connection(State)))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE PrReviews SET Feedback='old binary draft';";
            command.ExecuteNonQuery();
        }
        Assert.Equal("old binary draft", Assert.Single(await Current.GetReviewsAsync(Repo, 8, _ct)).Feedback);
        Assert.Equal(schema, Scalar(State, "PRAGMA schema_version;"));
        Assert.Equal(receipts, Scalar(State, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(3L, Scalar(State, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Newer_dedicated_generation_is_refused_without_changing_it()
    {
        using (var db = SqliteConnectionFactory.Open(Connection(Reviews)))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<VibeRails.Data.Abstractions.StorageException>(() => Current.ListAsync(Repo, _ct));
        Assert.Equal(2L, Scalar(Reviews, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(Reviews, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table';"));
    }

    [Fact]
    public async Task Legacy_created_after_first_use_and_later_legacy_updates_are_adopted()
    {
        var current = Current;
        Assert.Empty(await current.ListAsync(Repo, _ct));
        var old = Legacy;
        await old.StartReviewAsync(Repo, 3, Fields, "abc", _ct);
        Assert.Single(await current.ListAsync(Repo, _ct));
        await old.SaveDraftAsync(Repo, 3, new(Feedback: "late draft"), "late summary", _ct);
        Assert.Equal("late draft", Assert.Single(await current.GetReviewsAsync(Repo, 3, _ct)).Feedback);
        await old.CompleteReviewAsync(Repo, 3, PrReviewVerdict.RecommendApprove, false, "done", null, null, _ct);
        Assert.Equal(PrTrackerStatus.Complete, (await current.GetAsync(Repo, 3, _ct))!.Status);
        await old.DeleteAsync(Repo, 3, _ct);
        Assert.Null(await current.GetAsync(Repo, 3, _ct));
    }

    [Fact]
    public async Task Local_edits_and_deletions_are_not_overwritten_or_resurrected_by_unchanged_legacy_rows()
    {
        await Legacy.StartReviewAsync(Repo, 2, Fields, null, _ct);
        var current = Current;
        await current.SaveDraftAsync(Repo, 2, new(Feedback: "new draft"), null, _ct);
        Assert.Equal("new draft", Assert.Single(await Current.GetReviewsAsync(Repo, 2, _ct)).Feedback);
        Assert.Equal("", Assert.Single(await Legacy.GetReviewsAsync(Repo, 2, _ct)).Feedback);
        await current.DeleteAsync(Repo, 2, _ct);
        Assert.Empty(await Current.ListAsync(Repo, _ct));
        Assert.NotNull(await Legacy.GetAsync(Repo, 2, _ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Divergent_edits_or_deletes_report_conflict_and_preserve_both_copies(bool delete)
    {
        await Legacy.StartReviewAsync(Repo, 2, Fields, null, _ct);
        var current = Current;
        if (delete) await current.DeleteAsync(Repo, 2, _ct);
        else await current.SaveDraftAsync(Repo, 2, new(Feedback: "new draft"), null, _ct);
        await Legacy.SaveDraftAsync(Repo, 2, new(Feedback: "old draft"), null, _ct);

        var failure = await Assert.ThrowsAsync<PrReviewAdoptionConflictException>(() => Current.ListAsync(Repo, _ct));
        Assert.Equal(2, failure.Number);
        Assert.Contains("Both copies are preserved", failure.Message);
        Assert.Equal("old draft", Scalar(State, "SELECT Feedback FROM PrReviews;"));
        Assert.Equal(delete ? null : "new draft", Scalar(Reviews, "SELECT Feedback FROM PrReviews;"));
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewLegacyAdoptions;"));
    }

    [Fact]
    public async Task Legacy_and_new_review_ids_cannot_collide_and_mappings_stay_stable()
    {
        await Legacy.StartReviewAsync(Repo, 1, Fields, null, _ct);
        await Current.GetAsync(Repo, 1, _ct);
        var local = await Current.StartReviewAsync(Repo, 2, Fields, null, _ct);
        await Legacy.StartReviewAsync(Repo, 3, Fields, null, _ct); // Both files allocate legacy/local id 2.
        var adopted = Assert.Single(await Current.GetReviewsAsync(Repo, 3, _ct));
        Assert.NotEqual(local.Review!.Id, adopted.Id);
        await Legacy.SaveDraftAsync(Repo, 3, new(Feedback: "later"), null, _ct);
        Assert.Equal(adopted.Id, Assert.Single(await Current.GetReviewsAsync(Repo, 3, _ct)).Id);
        Assert.Equal(local.Review, Assert.Single(await Current.GetReviewsAsync(Repo, 2, _ct)));
    }

    [Fact]
    public async Task Concurrent_first_open_adopts_once_and_start_remains_idempotent()
    {
        await Legacy.StartReviewAsync(Repo, 1, Fields, null, _ct);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            start.Wait(_ct);
            return await Current.StartReviewAsync(Repo, 1, new(), null, _ct);
        }, _ct)).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(r => r.Review!.Id).Distinct());
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviews;"));
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewLegacyIds;"));
    }

    [Fact]
    public async Task Older_writer_can_remain_open_and_commit_after_adoption()
    {
        await Legacy.StartReviewAsync(Repo, 1, Fields, null, _ct);
        using var source = SqliteConnectionFactory.Open(Connection(State));
        using var write = source.BeginTransaction(deferred: false);
        using var update = source.CreateCommand();
        update.Transaction = write;
        update.CommandText = "UPDATE PrReviews SET Feedback='still writing';";
        update.ExecuteNonQuery();
        // WAL snapshot sees the committed baseline, without waiting for/locking the older writer.
        Assert.Equal("", Assert.Single(await Current.GetReviewsAsync(Repo, 1, _ct)).Feedback);
        write.Commit();
        Assert.Equal("still writing", Assert.Single(await Current.GetReviewsAsync(Repo, 1, _ct)).Feedback);
    }

    [Fact]
    public async Task Conflict_rolls_back_other_imports_and_can_retry_after_resolution()
    {
        await Legacy.StartReviewAsync(Repo, 2, Fields, null, _ct);
        await Current.SaveDraftAsync(Repo, 2, new(Feedback: "local"), null, _ct);
        await Legacy.StartReviewAsync(Repo, 1, Fields, null, _ct);
        await Legacy.SaveDraftAsync(Repo, 2, new(Feedback: "legacy"), null, _ct);
        await Assert.ThrowsAsync<PrReviewAdoptionConflictException>(() => Current.ListAsync(Repo, _ct));
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewPullRequests;"));
        Assert.Equal(1L, Scalar(Reviews, "SELECT COUNT(*) FROM PrReviewLegacyIds;"));
        // Restore the target's baseline to model an explicit reconciliation, then retry.
        using (var db = SqliteConnectionFactory.Open(Connection(Reviews)))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE PrReviews SET Feedback='', UpdatedUTC=(SELECT StartedUTC FROM PrReviews LIMIT 1);";
            // The original Start stores these timestamps in the same operation.
            cmd.ExecuteNonQuery();
        }
        // PR UpdatedUTC also changed locally; restoring it permits automatic retry.
        using (var db = SqliteConnectionFactory.Open(Connection(Reviews)))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE PrReviewPullRequests SET UpdatedUTC=CreatedUTC;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(2, (await Current.ListAsync(Repo, _ct)).Count);
        Assert.Equal("legacy", Assert.Single(await Current.GetReviewsAsync(Repo, 2, _ct)).Feedback);
    }

    private static string Connection(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    private static object? Scalar(string path, string sql)
    {
        using var db = SqliteConnectionFactory.Open(Connection(path));
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
