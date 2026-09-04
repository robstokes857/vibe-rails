using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class SessionDataExportRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"viberails-session-export-repository-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SessionDataExportRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "state.db");
        _connectionString = ConnectionString(_databasePath);
    }

    [Fact]
    public async Task MigrationAndEligibility_IgnoreLiveRecentAndExportedSessions_ThenMarkOldest()
    {
        var legacyPath = Path.Combine(_root, "legacy.db");
        var legacyConnectionString = ConnectionString(legacyPath);
        await CreateLegacySessionsTableAsync(legacyConnectionString);

        var repository = new Repository(legacyConnectionString);
        // A differently ordered equivalent connection string forces the idempotent migration pass
        // to execute again against the same file rather than hitting Repository's per-string cache.
        _ = new Repository(
            $"Mode=ReadWriteCreate;Cache=Shared;Data Source={legacyPath}");

        Assert.True(await ColumnExistsAsync(legacyConnectionString, "Sessions", "ExportedUTC"));
        Assert.True(await IndexExistsAsync(legacyConnectionString, "idx_sessions_unexported"));

        var cutoff = Utc(2026, 9, 3, 12, 0, 0);
        // Later than every deferral these rows could carry, so selection is exercised on
        // export eligibility alone. The backoff window itself is covered separately below.
        var sweepNow = cutoff.AddDays(1);
        var alreadyExported = "00000000-0000-0000-0000-000000000001";
        var firstEligible = "00000000-0000-0000-0000-000000000002";
        var secondEligible = "00000000-0000-0000-0000-000000000003";
        var tooRecent = "00000000-0000-0000-0000-000000000004";
        var live = "00000000-0000-0000-0000-000000000005";

        await InsertSessionAsync(
            legacyConnectionString,
            alreadyExported,
            cutoff.AddHours(-2),
            processed: 0,
            exportedUtc: cutoff.AddHours(-1));
        // Processed deliberately differs: transcript processing must not participate in export
        // eligibility or be changed by the export acknowledgement.
        await InsertSessionAsync(
            legacyConnectionString,
            secondEligible,
            cutoff.AddMinutes(-10),
            processed: 0);
        await InsertSessionAsync(
            legacyConnectionString,
            firstEligible,
            cutoff.AddMinutes(-10),
            processed: 1);
        await InsertSessionAsync(
            legacyConnectionString,
            tooRecent,
            cutoff.AddTicks(1),
            processed: 0);
        await InsertSessionAsync(
            legacyConnectionString,
            live,
            endedUtc: null,
            processed: 0);

        Assert.Equal(
            firstEligible,
            (await repository.GetOldestUnexportedSessionAsync(
                cutoff,
                sweepNow,
                TestContext.Current.CancellationToken))?.SessionId);

        var markedUtc = cutoff.AddMinutes(5);
        Assert.True(await repository.MarkSessionExportedAsync(
            firstEligible,
            markedUtc,
            TestContext.Current.CancellationToken));
        Assert.False(await repository.MarkSessionExportedAsync(
            firstEligible,
            markedUtc.AddMinutes(1),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            secondEligible,
            (await repository.GetOldestUnexportedSessionAsync(
                cutoff,
                sweepNow,
                TestContext.Current.CancellationToken))?.SessionId);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                legacyConnectionString,
                "SELECT Processed FROM Sessions WHERE Id = $id;",
                ("$id", firstEligible)));
        Assert.Equal(
            markedUtc.ToString("O"),
            await ScalarAsync<string>(
                legacyConnectionString,
                "SELECT ExportedUTC FROM Sessions WHERE Id = $id;",
                ("$id", firstEligible)));

        Assert.True(await repository.MarkSessionExportedAsync(
            secondEligible,
            markedUtc,
            TestContext.Current.CancellationToken));
        Assert.Null((await repository.GetOldestUnexportedSessionAsync(
            cutoff,
            sweepNow,
            TestContext.Current.CancellationToken))?.SessionId);
    }

    [Fact]
    public async Task WriteSessionExport_StreamsInsteadOfBufferingTheWholeEnvelope()
    {
        var repository = new Repository(_connectionString);
        var sessionId = "33333333-4444-5555-6666-777777777777";

        await SeedCompleteSessionAsync(
            _connectionString,
            sessionId,
            parentId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            startedUtc: Utc(2026, 9, 3, 10, 0, 0),
            endedUtc: Utc(2026, 9, 3, 11, 0, 0),
            firstSessionBytes: new byte[] { 1, 2 },
            secondSessionBytes: new byte[] { 3, 4 },
            firstTerminalBytes: new byte[] { 5, 6 },
            secondTerminalBytes: new byte[] { 7, 8 });

        // Many rows each well under the 64 KiB flush threshold, so the threshold - not any single
        // row - is what bounds a write. One oversized row would not discriminate: the writer
        // cannot flush inside WriteBase64String, so the bound is always "threshold + one row".
        const int extraRows = 60;
        const int rowBytes = 4 * 1024;
        var payload = new byte[rowBytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        for (var row = 0; row < extraRows; row++)
        {
            await ExecuteAsync(
                _connectionString,
                """
                INSERT INTO TerminalSessionLogs
                    (Id, SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp)
                VALUES ($id, $sessionId, $sequence, 0, $data, 80, 24, $timestamp);
                """,
                ("$id", 1000 + row),
                ("$sessionId", sessionId),
                ("$sequence", 10 + row),
                ("$data", payload),
                ("$timestamp", $"2026-09-03T11:{row:D2}:00.0000000Z"));
        }

        await using var destination = new CountingStream();
        var descriptor = await repository.WriteSessionExportAsync(
            sessionId,
            destination,
            TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);

        // The point of the fix: bytes reach the compressor in bounded instalments during the read.
        // Buffering the whole envelope and flushing once at the end delivers it in a single write
        // whose size is the entire document - which is exactly the unbounded allocation the fix
        // removed, and what this assertion rules out.
        Assert.True(
            destination.MaxWrite < destination.Length / 2,
            $"A single write carried {destination.MaxWrite} of {destination.Length} bytes; "
                + "the envelope was buffered rather than streamed.");
        Assert.True(
            destination.WriteCount >= 3,
            $"Expected several bounded writes, saw {destination.WriteCount}.");

        // Flushing mid-array and mid-object must not corrupt the document or lose a row.
        using var document = JsonDocument.Parse(destination.ToArray());
        var terminal = document.RootElement.GetProperty("terminalSessionLogs");
        Assert.Equal(extraRows + 2, terminal.GetArrayLength());
        Assert.Equal(
            payload,
            terminal[terminal.GetArrayLength() - 1].GetProperty("rawBytes").GetBytesFromBase64());

        // The userInputs grouping watermark is spanned by the same flush call, so prove the
        // groups still came out whole.
        var inputs = document.RootElement.GetProperty("userInputs");
        Assert.Equal(3, inputs.GetArrayLength());
        Assert.Equal(2, inputs[0].GetProperty("fileChanges").GetArrayLength());
        Assert.Equal(0, inputs[2].GetProperty("fileChanges").GetArrayLength());
    }

    [Fact]
    public async Task DeferSessionExport_YieldsTheQueueHeadUntilTheBackoffElapses()
    {
        var repository = new Repository(_connectionString);
        var cutoff = Utc(2026, 9, 3, 12, 0, 0);
        var blocked = "00000000-0000-0000-0000-0000000000a1";
        var behind = "00000000-0000-0000-0000-0000000000a2";

        await InsertSessionAsync(_connectionString, blocked, cutoff.AddMinutes(-30), processed: 0);
        await InsertSessionAsync(_connectionString, behind, cutoff.AddMinutes(-10), processed: 0);

        var now = cutoff;
        var head = await repository.GetOldestUnexportedSessionAsync(
            cutoff, now, TestContext.Current.CancellationToken);
        Assert.Equal(blocked, head?.SessionId);
        Assert.Equal(0, head?.Attempts);

        var retryAt = now.AddMinutes(2);
        Assert.True(await repository.DeferSessionExportAsync(
            blocked, retryAt, TestContext.Current.CancellationToken));

        // The newer session is now reachable: one failing session no longer starves the queue.
        Assert.Equal(
            behind,
            (await repository.GetOldestUnexportedSessionAsync(
                cutoff, now, TestContext.Current.CancellationToken))?.SessionId);

        // Deferred, not abandoned - it comes back once the backoff elapses, carrying its count.
        var resumed = await repository.GetOldestUnexportedSessionAsync(
            cutoff, retryAt, TestContext.Current.CancellationToken);
        Assert.Equal(blocked, resumed?.SessionId);
        Assert.Equal(1, resumed?.Attempts);

        // Deferral never acknowledges: ExportedUTC stays NULL so no data is silently dropped.
        // Asserted as a predicate rather than a scalar read, because ScalarAsync coerces a SQL
        // NULL to "" rather than null.
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                _connectionString,
                "SELECT COUNT(*) FROM Sessions WHERE Id = $id AND ExportedUTC IS NULL;",
                ("$id", blocked)));

        Assert.True(await repository.MarkSessionExportedAsync(
            blocked, retryAt, TestContext.Current.CancellationToken));
        Assert.False(await repository.DeferSessionExportAsync(
            blocked, retryAt.AddMinutes(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionAwaitsExport_TracksSpoolRetentionAcrossExportAndDeletion()
    {
        var repository = new Repository(_connectionString);
        var cutoff = Utc(2026, 9, 3, 12, 0, 0);
        var pending = "00000000-0000-0000-0000-0000000000b1";

        await InsertSessionAsync(_connectionString, pending, cutoff.AddMinutes(-5), processed: 0);
        Assert.True(await repository.SessionAwaitsExportAsync(
            pending, TestContext.Current.CancellationToken));

        Assert.True(await repository.MarkSessionExportedAsync(
            pending, cutoff, TestContext.Current.CancellationToken));
        // Exported: the spool for it is now reclaimable.
        Assert.False(await repository.SessionAwaitsExportAsync(
            pending, TestContext.Current.CancellationToken));

        // A row the user deleted outright can never be selected again, so its spool is orphaned.
        Assert.False(await repository.SessionAwaitsExportAsync(
            "00000000-0000-0000-0000-0000000000b2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteSessionExport_RoundTripsEveryPayloadAndIsDeterministicUntilMarked()
    {
        var repository = new Repository(_connectionString);
        var sessionId = "11111111-2222-3333-4444-555555555555";
        var parentId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var startedUtc = Utc(2026, 9, 3, 10, 0, 0);
        var endedUtc = startedUtc.AddMinutes(12);
        var firstSessionBytes = new byte[] { 0x00, 0xff, 0x1b, 0x5b, 0x33, 0x31, 0x6d };
        var secondSessionBytes = Encoding.UTF8.GetBytes("stderr\r\n");
        var firstTerminalBytes = new byte[] { 0x1b, 0x5b, 0x3f, 0x31, 0x30, 0x34, 0x39, 0x68 };
        var secondTerminalBytes = new byte[] { 0x00, 0x0a, 0x0d, 0xff };

        await SeedCompleteSessionAsync(
            _connectionString,
            sessionId,
            parentId,
            startedUtc,
            endedUtc,
            firstSessionBytes,
            secondSessionBytes,
            firstTerminalBytes,
            secondTerminalBytes);

        await using var first = new MemoryStream();
        var descriptor = await repository.WriteSessionExportAsync(
            sessionId,
            first,
            TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal(1, descriptor!.SchemaVersion);
        Assert.Equal("session", descriptor.Kind);
        Assert.Equal(Guid.Parse(sessionId), descriptor.SourceId);

        var firstJson = first.ToArray();
        using (var document = JsonDocument.Parse(firstJson))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("session", root.GetProperty("kind").GetString());
            Assert.Equal(Guid.Parse(sessionId), root.GetProperty("sourceId").GetGuid());

            var session = root.GetProperty("session");
            Assert.Equal(sessionId, session.GetProperty("id").GetString());
            Assert.Equal("codex", session.GetProperty("cli").GetString());
            Assert.Equal("Research", session.GetProperty("environmentName").GetString());
            Assert.Equal(@"C:\source\sample", session.GetProperty("workingDirectory").GetString());
            Assert.Equal("Sample project", session.GetProperty("projectDisplayName").GetString());
            Assert.Equal(startedUtc.ToString("O"), session.GetProperty("startedUtc").GetString());
            Assert.Equal(endedUtc.ToString("O"), session.GetProperty("endedUtc").GetString());
            Assert.Equal(17, session.GetProperty("exitCode").GetInt32());
            Assert.Equal(parentId, session.GetProperty("parentSessionId").GetString());
            Assert.Equal("Investigate bytes", session.GetProperty("sessionDisplayName").GetString());
            Assert.Equal("job-run-42", session.GetProperty("jobRunId").GetString());
            Assert.False(session.TryGetProperty("processed", out _));
            Assert.False(session.TryGetProperty("ownerPid", out _));
            Assert.False(session.TryGetProperty("exportedUtc", out _));

            var sessionLogs = SessionArray(root, "sessionLogs");
            Assert.Equal(2, sessionLogs.Length);
            Assert.Equal(11, sessionLogs[0].GetProperty("id").GetInt64());
            Assert.Equal(firstSessionBytes, sessionLogs[0].GetProperty("rawBytes").GetBytesFromBase64());
            Assert.False(sessionLogs[0].GetProperty("isError").GetBoolean());
            Assert.Equal(12, sessionLogs[1].GetProperty("id").GetInt64());
            Assert.Equal(secondSessionBytes, sessionLogs[1].GetProperty("rawBytes").GetBytesFromBase64());
            Assert.True(sessionLogs[1].GetProperty("isError").GetBoolean());

            var terminalLogs = SessionArray(root, "terminalSessionLogs");
            Assert.Equal(2, terminalLogs.Length);
            Assert.Equal(1, terminalLogs[0].GetProperty("sequence").GetInt32());
            Assert.Equal(firstTerminalBytes, terminalLogs[0].GetProperty("rawBytes").GetBytesFromBase64());
            Assert.True(terminalLogs[0].GetProperty("isAlternateScreen").GetBoolean());
            Assert.Equal(132, terminalLogs[0].GetProperty("cols").GetInt32());
            Assert.Equal(43, terminalLogs[0].GetProperty("rows").GetInt32());
            Assert.Equal(2, terminalLogs[1].GetProperty("sequence").GetInt32());
            Assert.Equal(secondTerminalBytes, terminalLogs[1].GetProperty("rawBytes").GetBytesFromBase64());
            Assert.False(terminalLogs[1].GetProperty("isAlternateScreen").GetBoolean());
            Assert.Equal(80, terminalLogs[1].GetProperty("cols").GetInt32());
            Assert.Equal(24, terminalLogs[1].GetProperty("rows").GetInt32());

            var inputs = SessionArray(root, "userInputs");
            Assert.Equal(3, inputs.Length);
            Assert.Equal(1, inputs[0].GetProperty("sequence").GetInt32());
            Assert.Equal("first input\nwith newline", inputs[0].GetProperty("inputText").GetString());
            Assert.Equal("commit-one", inputs[0].GetProperty("gitCommitHash").GetString());
            var firstChanges = SessionArray(inputs[0], "fileChanges");
            Assert.Equal(2, firstChanges.Length);
            Assert.Equal(41, firstChanges[0].GetProperty("id").GetInt64());
            Assert.Equal("src/one.cs", firstChanges[0].GetProperty("filePath").GetString());
            Assert.Equal("M", firstChanges[0].GetProperty("changeType").GetString());
            Assert.Equal(5, firstChanges[0].GetProperty("linesAdded").GetInt32());
            Assert.Equal(2, firstChanges[0].GetProperty("linesDeleted").GetInt32());
            Assert.Equal("@@ exact diff one @@", firstChanges[0].GetProperty("diffContent").GetString());
            Assert.Equal(42, firstChanges[1].GetProperty("id").GetInt64());
            Assert.Equal(JsonValueKind.Null, firstChanges[1].GetProperty("linesAdded").ValueKind);
            Assert.Equal(JsonValueKind.Null, firstChanges[1].GetProperty("linesDeleted").ValueKind);
            Assert.Equal(JsonValueKind.Null, firstChanges[1].GetProperty("diffContent").ValueKind);

            Assert.Equal(2, inputs[1].GetProperty("sequence").GetInt32());
            Assert.Equal(JsonValueKind.Null, inputs[1].GetProperty("gitCommitHash").ValueKind);
            var secondChanges = SessionArray(inputs[1], "fileChanges");
            var secondChange = Assert.Single(secondChanges);
            Assert.Equal(31, secondChange.GetProperty("previousInputId").GetInt64());
            Assert.Equal("D", secondChange.GetProperty("changeType").GetString());
            Assert.Equal("deleted file diff", secondChange.GetProperty("diffContent").GetString());

            Assert.Equal(3, inputs[2].GetProperty("sequence").GetInt32());
            Assert.Empty(SessionArray(inputs[2], "fileChanges"));
            Assert.Equal("full transcript\nline two", root.GetProperty("transcript").GetString());
            var summary = root.GetProperty("summary");
            Assert.Equal("A precise summary", summary.GetProperty("summaryText").GetString());
            Assert.Equal("2026-09-03T10:13:00.0000000Z", summary.GetProperty("dateUtc").GetString());
        }

        await using var repeated = new MemoryStream();
        var repeatedDescriptor = await repository.WriteSessionExportAsync(
            sessionId,
            repeated,
            TestContext.Current.CancellationToken);
        Assert.Equal(descriptor, repeatedDescriptor);
        Assert.Equal(firstJson, repeated.ToArray());

        var markedUtc = endedUtc.AddHours(1);
        Assert.True(await repository.MarkSessionExportedAsync(
            sessionId,
            markedUtc,
            TestContext.Current.CancellationToken));
        Assert.False(await repository.MarkSessionExportedAsync(
            sessionId,
            markedUtc.AddSeconds(1),
            TestContext.Current.CancellationToken));

        await using var afterMark = new MemoryStream();
        Assert.Null(await repository.WriteSessionExportAsync(
            sessionId,
            afterMark,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, afterMark.Length);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                _connectionString,
                "SELECT Processed FROM Sessions WHERE Id = $id;",
                ("$id", sessionId)));
        Assert.Equal(
            markedUtc.ToString("O"),
            await ScalarAsync<string>(
                _connectionString,
                "SELECT ExportedUTC FROM Sessions WHERE Id = $id;",
                ("$id", sessionId)));
    }

    [Fact]
    public async Task WriteSessionExport_WalReadTransaction_DoesNotBlockConcurrentWriter()
    {
        var repository = new Repository(_connectionString);
        var sessionId = "99999999-8888-7777-6666-555555555555";
        var endedUtc = Utc(2026, 9, 3, 9, 0, 0);
        await InsertSessionAsync(_connectionString, sessionId, endedUtc, processed: 0);
        await ExecuteAsync(
            _connectionString,
            """
            INSERT INTO SessionLogs (Id, SessionId, Timestamp, Content, IsError)
            VALUES (1, $sessionId, $timestamp, $content, 0);
            """,
            ("$sessionId", sessionId),
            ("$timestamp", endedUtc.ToString("O")),
            ("$content", new byte[] { 1, 2, 3 }));

        await using var destination = new BlockingWriteStream();
        var exportTask = repository.WriteSessionExportAsync(
            sessionId,
            destination,
            TestContext.Current.CancellationToken);
        await destination.WaitUntilWriteAsync(TestContext.Current.CancellationToken);

        var concurrentWrite = ExecuteAsync(
            _connectionString,
            """
            INSERT INTO SessionLogs (Id, SessionId, Timestamp, Content, IsError)
            VALUES (2, $sessionId, $timestamp, $content, 0);
            """,
            ("$sessionId", sessionId),
            ("$timestamp", endedUtc.AddSeconds(1).ToString("O")),
            ("$content", new byte[] { 4, 5, 6 }));

        try
        {
            var completed = await Task.WhenAny(
                concurrentWrite,
                Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Same(concurrentWrite, completed);
            await concurrentWrite;
        }
        finally
        {
            destination.Release();
        }

        Assert.NotNull(await exportTask);
        using var document = JsonDocument.Parse(destination.ToArray());
        var exportedLogs = SessionArray(document.RootElement, "sessionLogs");
        Assert.Single(exportedLogs);
        Assert.Equal(1, exportedLogs[0].GetProperty("id").GetInt64());
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                _connectionString,
                "SELECT COUNT(*) FROM SessionLogs WHERE SessionId = $sessionId;",
                ("$sessionId", sessionId)));
    }

    private static JsonElement[] SessionArray(JsonElement parent, string name) =>
        parent.GetProperty(name).EnumerateArray().ToArray();

    private static string ConnectionString(string path) =>
        $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private static async Task CreateLegacySessionsTableAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Sessions (
                Id TEXT PRIMARY KEY,
                Cli TEXT NOT NULL,
                EnvironmentName TEXT,
                WorkingDirectory TEXT NOT NULL,
                ProjectDisplayName TEXT NOT NULL DEFAULT '',
                StartedUTC TEXT NOT NULL,
                EndedUTC TEXT,
                ExitCode INTEGER,
                Processed INTEGER NOT NULL DEFAULT 0,
                ParentSessionId TEXT DEFAULT '',
                SessionDisplayName TEXT DEFAULT '',
                OwnerPid INTEGER,
                OwnershipTracked INTEGER NOT NULL DEFAULT 1,
                JobRunId TEXT
            );
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertSessionAsync(
        string connectionString,
        string sessionId,
        DateTime? endedUtc,
        int processed,
        DateTime? exportedUtc = null)
    {
        var startedUtc = (endedUtc ?? Utc(2026, 9, 3, 12, 0, 0)).AddMinutes(-1);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO Sessions
                (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName,
                 StartedUTC, EndedUTC, ExitCode, Processed, ParentSessionId,
                 SessionDisplayName, OwnerPid, OwnershipTracked, JobRunId, ExportedUTC)
            VALUES
                ($id, 'codex', NULL, 'C:/source/project', 'Project',
                 $startedUtc, $endedUtc, 0, $processed, '', '', NULL, 1, NULL, $exportedUtc);
            """,
            ("$id", sessionId),
            ("$startedUtc", startedUtc.ToString("O")),
            ("$endedUtc", endedUtc?.ToString("O")),
            ("$processed", processed),
            ("$exportedUtc", exportedUtc?.ToString("O")));
    }

    private static async Task SeedCompleteSessionAsync(
        string connectionString,
        string sessionId,
        string parentId,
        DateTime startedUtc,
        DateTime endedUtc,
        byte[] firstSessionBytes,
        byte[] secondSessionBytes,
        byte[] firstTerminalBytes,
        byte[] secondTerminalBytes)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO Sessions
                (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName,
                 StartedUTC, EndedUTC, ExitCode, Processed, ParentSessionId,
                 SessionDisplayName, OwnerPid, OwnershipTracked, JobRunId, ExportedUTC)
            VALUES
                ($sessionId, 'codex', 'Research', 'C:\source\sample', 'Sample project',
                 $startedUtc, $endedUtc, 17, 1, $parentId,
                 'Investigate bytes', 4242, 1, 'job-run-42', NULL);

            INSERT INTO SessionLogs (Id, SessionId, Timestamp, Content, IsError)
            VALUES (12, $sessionId, '2026-09-03T10:02:00.0000000Z', $sessionBytes2, 1);
            INSERT INTO SessionLogs (Id, SessionId, Timestamp, Content, IsError)
            VALUES (11, $sessionId, '2026-09-03T10:01:00.0000000Z', $sessionBytes1, 0);

            INSERT INTO TerminalSessionLogs
                (Id, SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp)
            VALUES
                (22, $sessionId, 2, 0, $terminalBytes2, 80, 24,
                 '2026-09-03T10:04:00.0000000Z');
            INSERT INTO TerminalSessionLogs
                (Id, SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp)
            VALUES
                (21, $sessionId, 1, 1, $terminalBytes1, 132, 43,
                 '2026-09-03T10:03:00.0000000Z');

            INSERT INTO UserInputs
                (Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
            VALUES
                (32, $sessionId, 2, 'second input', NULL,
                 '2026-09-03T10:06:00.0000000Z');
            INSERT INTO UserInputs
                (Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
            VALUES
                (31, $sessionId, 1, $firstInput, 'commit-one',
                 '2026-09-03T10:05:00.0000000Z');
            INSERT INTO UserInputs
                (Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
            VALUES
                (33, $sessionId, 3, 'no file changes', 'commit-three',
                 '2026-09-03T10:07:00.0000000Z');

            INSERT INTO InputFileChanges
                (Id, UserInputId, PreviousInputId, FilePath, ChangeType,
                 LinesAdded, LinesDeleted, DiffContent)
            VALUES
                (42, 31, NULL, 'new/file.txt', 'A', NULL, NULL, NULL);
            INSERT INTO InputFileChanges
                (Id, UserInputId, PreviousInputId, FilePath, ChangeType,
                 LinesAdded, LinesDeleted, DiffContent)
            VALUES
                (41, 31, NULL, 'src/one.cs', 'M', 5, 2, '@@ exact diff one @@');
            INSERT INTO InputFileChanges
                (Id, UserInputId, PreviousInputId, FilePath, ChangeType,
                 LinesAdded, LinesDeleted, DiffContent)
            VALUES
                (43, 32, 31, 'old/file.cs', 'D', 0, 9, 'deleted file diff');

            INSERT INTO sessionOutPut (SessionId, Text)
            VALUES ($sessionId, $transcript);
            INSERT INTO ChatSummary (SessionId, SummaryText, Date)
            VALUES ($sessionId, 'A precise summary', '2026-09-03T10:13:00.0000000Z');
            """,
            ("$sessionId", sessionId),
            ("$parentId", parentId),
            ("$startedUtc", startedUtc.ToString("O")),
            ("$endedUtc", endedUtc.ToString("O")),
            ("$sessionBytes1", firstSessionBytes),
            ("$sessionBytes2", secondSessionBytes),
            ("$terminalBytes1", firstTerminalBytes),
            ("$terminalBytes2", secondTerminalBytes),
            ("$firstInput", "first input\nwith newline"),
            ("$transcript", "full transcript\nline two"));
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return (T)Convert.ChangeType(result, typeof(T));
    }

    private static async Task<bool> ColumnExistsAsync(
        string connectionString,
        string table,
        string column) =>
        await ScalarAsync<long>(
            connectionString,
            "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;",
            ("$table", table),
            ("$column", column)) == 1;

    private static async Task<bool> IndexExistsAsync(string connectionString, string index) =>
        await ScalarAsync<long>(
            connectionString,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $index;",
            ("$index", index)) == 1;

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A still-closing WAL handle on Windows; the uniquely named temp directory is safe
            // for the operating system's normal temp cleanup.
        }
    }

    /// <summary>
    /// Records the number and size of writes that reach the destination. A writer that buffers the
    /// whole envelope delivers it as one write the size of the document; a streaming one delivers
    /// several, each bounded by the flush threshold plus a row.
    /// </summary>
    private sealed class CountingStream : MemoryStream
    {
        public int WriteCount { get; private set; }
        public int MaxWrite { get; private set; }

        private void Record(int count)
        {
            WriteCount++;
            if (count > MaxWrite) MaxWrite = count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Record(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Record(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Record(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public async Task WaitUntilWriteAsync(CancellationToken cancellationToken) =>
            await _writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        public void Release() => _release.TrySetResult();
        public byte[] ToArray() => _inner.ToArray();

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _writeStarted.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}
