using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class ConversationTitleStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conversation-titles-{Guid.NewGuid():N}.db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimed_AndOldCompletionCannotOverwriteNewTitle()
    {
        var firstStore = new ConversationTitleStore(ConnectionString);
        Seed();
        var secondStore = new ConversationTitleStore(ConnectionString);
        var first = Assert.IsType<ConversationTitleWork>(await firstStore.ClaimAsync(Now, Ct));
        Assert.Null(await secondStore.ClaimAsync(Now.AddSeconds(30), Ct));
        var second = Assert.IsType<ConversationTitleWork>(await secondStore.ClaimAsync(Now.AddMinutes(3), Ct));
        Assert.NotEqual(first.Lease, second.Lease);
        await firstStore.CompleteAsync(first, "Stale title", Now.AddMinutes(3), Ct, "stale-hash");
        Assert.Equal("", Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.Equal(DBNull.Value, Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        await secondStore.CompleteAsync(second, "Fix login after refresh", Now.AddMinutes(3), Ct, "current-hash");
        Assert.Equal("Fix login after refresh", Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.Equal("current-hash", Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        Assert.Null(await firstStore.ClaimAsync(Now.AddDays(1), Ct));
    }

    [Fact]
    public async Task ChangedNameWinsOverInflightTitle_EvenForLegacyWriters()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        Execute("UPDATE Sessions SET SessionDisplayName='My chosen name' WHERE Id='session'");
        await store.CompleteAsync(work, "Generated name", Now.AddSeconds(1), Ct, "unapplied-hash");
        Assert.Equal("My chosen name", Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.Equal(DBNull.Value, Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
    }

    [Fact]
    public async Task FailedRequestWaitsForRetry_AndCanFinishWithoutChangingName()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        await store.DeferAsync(work, Now.AddHours(1), Ct);
        Assert.Null(await store.ClaimAsync(Now.AddMinutes(59), Ct));
        var retry = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now.AddHours(1), Ct));
        Assert.Equal(2, retry.Attempts);
        await store.CompleteAsync(retry, null, Now.AddHours(1), Ct);
        Assert.Equal("", Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.Null(await store.ClaimAsync(Now.AddDays(1), Ct));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Please fix login after refresh", true)]
    [InlineData("My chosen name", false)]
    public async Task RecognizesOnlyEmptyOrFirstPromptAutomaticNames(string name, bool eligible)
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        using (var db = SqliteConnectionFactory.Open(ConnectionString))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE Sessions SET SessionDisplayName=$name";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        Assert.Equal(eligible, work.Eligible);
        await store.CompleteAsync(work, "New title", Now, Ct);
        Assert.Equal(eligible ? "New title" : name, Scalar("SELECT SessionDisplayName FROM Sessions"));
    }

    [Fact]
    public void AdditiveSetupDoesNotEnrollExistingHistory_AndLegacyWritesStillWork()
    {
        _ = new ConversationTitleStore(ConnectionString);
        Seed(enqueue: false);
        StateDatabaseSchema.Ensure(ConnectionString);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM ConversationTitleRequests"));
        Execute("UPDATE Sessions SET SessionDisplayName='Legacy name'");
        Assert.Equal("Legacy name", Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.Equal((long)StateDatabaseSchema.Generation, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task ExcerptHashUpgradePreservesRowsAndLegacyWriterWhileOpen()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        Seed();
        // Recreate the shipped v2 shape in this disposable fixture only.
        Execute("""
            ALTER TABLE ConversationTitleRequests DROP COLUMN LastExcerptHash;
            DELETE FROM SchemaMigrations WHERE Component='conversation-titles' AND Version=3;
            """);
        using var legacy = SqliteConnectionFactory.Open(ConnectionString);
        var store = new ConversationTitleStore(ConnectionString);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM ConversationTitleRequests"));
        Assert.Equal(DBNull.Value, Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
        Assert.Equal((long)StateDatabaseSchema.Generation, Scalar("PRAGMA user_version"));
        var schemaVersion = Scalar("PRAGMA schema_version");
        _ = new ConversationTitleStore(ConnectionString);
        Assert.Equal(schemaVersion, Scalar("PRAGMA schema_version"));

        // An already-open old writer can keep updating its known columns.
        using var update = legacy.CreateCommand();
        update.CommandText = "UPDATE ConversationTitleRequests SET CharacterCount=750, LatestInputId=LatestInputId+1";
        Assert.Equal(1, await update.ExecuteNonQueryAsync(Ct));
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        Assert.Null(first.LastExcerptHash);
        Assert.True(await store.CompleteAsync(first, "First title", Now, Ct, "saved-hash"));
        Execute("UPDATE ConversationTitleRequests SET LatestInputId=LatestInputId+1");
        var refresh = Assert.IsType<ConversationTitleWork>(await new ConversationTitleStore(ConnectionString).ClaimAsync(Now, Ct));
        Assert.Equal("saved-hash", refresh.LastExcerptHash);
    }

    [Fact]
    public async Task ExpiredUnchangedRefreshCannotEndReplacementLease()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        await store.CompleteAsync(first, "First title", Now, Ct, "saved-hash");
        Execute("UPDATE ConversationTitleRequests SET CharacterCount=750, LatestInputId=LatestInputId+1");
        var refresh = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        var replacement = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now.AddMinutes(3), Ct));

        await store.SkipUnchangedRefreshAsync(refresh, Now.AddMinutes(3), Ct);

        Assert.Equal(0L, Scalar("SELECT Cancelled FROM ConversationTitleRequests"));
        Assert.Equal(replacement.Lease, Scalar("SELECT Lease FROM ConversationTitleRequests"));
        Assert.True(await store.CompleteAsync(replacement, "Changed title", Now.AddMinutes(3), Ct, "new-hash"));
        Assert.Equal("new-hash", Scalar("SELECT LastExcerptHash FROM ConversationTitleRequests"));
    }

    [Fact]
    public async Task DeletingCapturedInputRemovesPendingWork()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        Execute("DELETE FROM UserInputs WHERE SessionId='session'");
        Assert.Null(await store.ClaimAsync(Now, Ct));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM ConversationTitleRequests"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitSameTextRenameCancelsPendingAndInflightWork(bool useHistoryRename, bool claimFirst)
    {
        var store = new ConversationTitleStore(ConnectionString);
        var repository = new Repository(ConnectionString);
        Seed();
        const string preview = "Please fix login after refresh";
        await repository.SaveSessionOutputAndMarkProcessedAsync("session", "transcript", Ct);
        var work = claimFirst ? await store.ClaimAsync(Now, Ct) : null;

        if (useHistoryRename)
            Assert.True(await repository.UpdateChatHistorySessionNameAsync("session", preview, Ct));
        else
            await repository.SetSessionDisplayNameAsync("session", preview);

        if (work is not null)
        {
            await store.CompleteAsync(work, "Generated name", Now.AddSeconds(1), Ct);
            await store.DeferAsync(work, Now.AddSeconds(2), Ct);
        }
        Assert.Equal(preview, Scalar("SELECT SessionDisplayName FROM Sessions"));
        Assert.NotEqual(DBNull.Value, Scalar("SELECT CompletedUTC FROM ConversationTitleRequests"));
        Assert.Null(await store.ClaimAsync(Now.AddDays(1), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptProcessingBeforeOrAfterCompletionPreservesGeneratedTitle(bool processFirst)
    {
        var store = new ConversationTitleStore(ConnectionString);
        var repository = new Repository(ConnectionString);
        Seed();
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        if (processFirst)
            await repository.SaveSessionOutputAndMarkProcessedAsync("session", "transcript", Ct);

        await store.CompleteAsync(work, "Fix login after refresh", Now.AddSeconds(1), Ct);
        await repository.SaveSessionOutputAndMarkProcessedAsync("session", "transcript", Ct);

        Assert.Equal("Fix login after refresh", Scalar("SELECT SessionDisplayName FROM Sessions"));
        var (_, displayName) = await repository.GetSessionDisplayInfoAsync("session");
        Assert.Equal("Fix login after refresh", displayName);
    }

    [Theory]
    [InlineData("JobRunId='job'")]
    [InlineData("ParentSessionId='parent'")]
    [InlineData("Cli='shell'")]
    public async Task EligibilityIsRecheckedWhenCompletingWork(string assignment)
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        Execute($"UPDATE Sessions SET {assignment} WHERE Id='session'");
        await store.CompleteAsync(work, "Generated name", Now.AddSeconds(1), Ct);
        Assert.Equal("", Scalar("SELECT SessionDisplayName FROM Sessions"));
    }

    [Fact]
    public async Task StaleDeferralCannotReleaseAnotherInstancesLease()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        var second = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now.AddMinutes(3), Ct));
        await store.DeferAsync(first, Now.AddMinutes(4), Ct);
        Assert.Null(await store.ClaimAsync(Now.AddMinutes(4), Ct));
        await store.CompleteAsync(second, "Current title", Now.AddMinutes(4), Ct);
        Assert.Equal("Current title", Scalar("SELECT SessionDisplayName FROM Sessions"));
    }

    [Fact]
    public async Task RenamingMissingSessionReportsFalse()
    {
        var repository = new Repository(ConnectionString);
        Assert.False(await repository.UpdateChatHistorySessionNameAsync("missing", "New name", Ct));
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public async Task FirstMilestoneUsesTrimmedCumulativeUserCharacters(int characters, bool due)
    {
        var repository = new Repository(ConnectionString);
        await repository.CreateSessionAsync("new", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("new", 1, "  " + new string('a', characters) + "  ", null);
        var store = new ConversationTitleStore(ConnectionString);
        Assert.Equal(due, await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct) is not null);
    }

    [Fact]
    public async Task TwoMessagesTriggerFirstTitle_ThenTenMessagesTriggerOnlyOneRefresh()
    {
        var repository = new Repository(ConnectionString);
        var store = new ConversationTitleStore(ConnectionString);
        await repository.CreateSessionAsync("new", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("new", 1, "Hi", null);
        Assert.Null(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct));
        await repository.InsertUserInputAsync("new", 2, "Help with login", null);
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct));
        Assert.True(await store.CompleteAsync(first, "Fix login", DateTime.UtcNow.AddSeconds(2), Ct));
        for (var message = 3; message <= 9; message++)
            await repository.InsertUserInputAsync("new", message, "More", null);
        Assert.Null(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(3), Ct));
        await repository.InsertUserInputAsync("new", 10, "Refresh fails", null);
        var refresh = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(3), Ct));
        Assert.Equal(1, refresh.Generation);
        Assert.True(await store.CompleteAsync(refresh, "Fix login after refresh", DateTime.UtcNow.AddSeconds(4), Ct));
        await repository.InsertUserInputAsync("new", 11, new string('z', 2000), null);
        Assert.Null(await store.ClaimAsync(DateTime.UtcNow.AddDays(1), Ct));
        Assert.Equal(2L, Scalar("SELECT GenerationCount FROM ConversationTitleRequests"));
    }

    [Theory]
    [InlineData(749, false)]
    [InlineData(750, true)]
    public async Task RefreshUsesCumulative750Characters(int characters, bool due)
    {
        var repository = new Repository(ConnectionString);
        var store = new ConversationTitleStore(ConnectionString);
        await repository.CreateSessionAsync("new", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("new", 1, new string('a', 300), null);
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct));
        await store.CompleteAsync(first, "First title", DateTime.UtcNow.AddSeconds(2), Ct);
        await repository.InsertUserInputAsync("new", 2, new string('b', characters - 300), null);
        Assert.Equal(due, await store.ClaimAsync(DateTime.UtcNow.AddSeconds(3), Ct) is not null);
    }

    [Fact]
    public async Task LongOpeningPromptDoesNotTriggerBothGenerationsWithUnchangedInput()
    {
        var repository = new Repository(ConnectionString);
        var store = new ConversationTitleStore(ConnectionString);
        await repository.CreateSessionAsync("new", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("new", 1, new string('a', 1000), null);
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct));
        await store.CompleteAsync(first, "First title", DateTime.UtcNow.AddSeconds(2), Ct);
        Assert.Null(await store.ClaimAsync(DateTime.UtcNow.AddDays(1), Ct));
    }

    [Fact]
    public async Task ManualNameBeforeCaptureAndHistoricalSessionsAreNeverEnrolled()
    {
        var repository = new Repository(ConnectionString);
        Seed(enqueue: false);
        await repository.InsertUserInputAsync("session", 2, new string('h', 1000), null);
        await repository.CreateSessionAsync("manual", "codex", null, ".", 1);
        await repository.SetSessionDisplayNameAsync("manual", "");
        await repository.InsertUserInputAsync("manual", 1, new string('a', 1000), null);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM ConversationTitleRequests"));
    }

    [Fact]
    public async Task SameTextRenameAfterFirstTitlePreventsRefreshAndClearsGeneratedMarker()
    {
        var repository = new Repository(ConnectionString);
        var store = new ConversationTitleStore(ConnectionString);
        await repository.CreateSessionAsync("new", "codex", null, ".", 1);
        await repository.InsertUserInputAsync("new", 1, new string('a', 300), null);
        var first = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(DateTime.UtcNow.AddSeconds(1), Ct));
        await store.CompleteAsync(first, "My title", DateTime.UtcNow.AddSeconds(2), Ct);
        Assert.True((await repository.GetChatHistoryItemAsync("new", Ct))!.SessionTitleGenerated);
        Assert.True(await repository.UpdateChatHistorySessionNameAsync("new", "My title", Ct));
        Assert.False((await repository.GetChatHistoryItemAsync("new", Ct))!.SessionTitleGenerated);
        await repository.InsertUserInputAsync("new", 2, new string('b', 1000), null);
        Assert.Null(await store.ClaimAsync(DateTime.UtcNow.AddDays(1), Ct));
    }

    [Fact]
    public async Task BackoffIsSharedAcrossInstancesAndNewKeysCanRetryImmediately()
    {
        var store = new ConversationTitleStore(ConnectionString);
        Seed();
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct, "key-a-hash"));
        await store.DeferAsync(work, Now.AddHours(1), Ct, "key-a-hash");
        var other = new ConversationTitleStore(ConnectionString);
        Assert.Null(await other.ClaimAsync(Now.AddMinutes(1), Ct, "key-a-hash"));
        Assert.NotNull(await other.ClaimAsync(Now.AddMinutes(1), Ct, "key-b-hash"));
    }

    [Fact]
    public async Task SavedTitleChangeIsVisibleToOtherRootInstances()
    {
        var store = new ConversationTitleStore(ConnectionString);
        var other = new ConversationTitleStore(ConnectionString);
        Seed();
        Assert.Null(await other.GetChangeStampAsync(Ct));
        var work = Assert.IsType<ConversationTitleWork>(await store.ClaimAsync(Now, Ct));
        await store.CompleteAsync(work, "Generated title", Now.AddSeconds(1), Ct);
        Assert.Equal(Now.AddSeconds(1).ToString("O"), await other.GetChangeStampAsync(Ct));
    }

    private void Seed(bool enqueue = true)
    {
        Execute("""
            INSERT INTO Sessions(Id,Cli,WorkingDirectory,StartedUTC) VALUES('session','codex','.', '2026-10-12T11:00:00.0000000Z');
            INSERT INTO UserInputs(SessionId,Sequence,InputText,TimestampUTC,SearchComponent)
            VALUES('session',1,'Please fix login after refresh','2026-10-12T11:00:01.0000000Z','search/1');
            """);
        if (enqueue) Execute("""
            INSERT INTO ConversationTitleRequests(SessionId,UserInputId,RetryAfterUTC,MessageCount,CharacterCount,LatestInputId)
            SELECT 'session',Id,'2026-10-12T11:00:01.0000000Z',2,29,Id FROM UserInputs;
            """);
    }

    private object? Scalar(string sql)
    {
        using var db = SqliteConnectionFactory.Open(ConnectionString);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Execute(string sql) => Scalar(sql);

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}
