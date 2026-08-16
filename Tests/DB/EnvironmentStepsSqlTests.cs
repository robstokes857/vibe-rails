using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

public class EnvironmentStepsSqlTests
{
    [Fact]
    public async Task Step_RoundTripsThroughInsertAndSelect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenSchemaAsync(cancellationToken);
        var environmentId = await InsertEnvironmentAsync(connection, "Nightly", LLM.Claude, cancellationToken);

        var stepId = Guid.NewGuid().ToString();
        await InsertStepAsync(
            connection,
            environmentId,
            EnvironmentStepPhase.PreLaunch,
            position: 0,
            name: "Install",
            command: "npm install",
            startMinimized: true,
            timeoutSeconds: 900,
            enabled: false,
            cancellationToken,
            id: stepId);

        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectStepsByEnvironmentId;
        select.Parameters.AddWithValue("$environmentId", environmentId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));
        // Ordinals matter: Repository.ReadEnvironmentStep reads these positionally, so a column
        // added in the wrong place in one statement silently misreads every step.
        // Id is the client-generated GUID string, stored verbatim.
        Assert.Equal(stepId, reader.GetString(0));
        Assert.Equal(environmentId, reader.GetInt32(1));
        Assert.Equal((int)EnvironmentStepPhase.PreLaunch, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal("Install", reader.GetString(4));
        Assert.Equal("npm install", reader.GetString(5));
        Assert.True(reader.GetBoolean(6));
        Assert.Equal(900, reader.GetInt32(7));
        Assert.False(reader.GetBoolean(8));
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    [Fact]
    public async Task Select_OrdersByPhaseThenPosition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenSchemaAsync(cancellationToken);
        var environmentId = await InsertEnvironmentAsync(connection, "Nightly", LLM.Claude, cancellationToken);

        // Deliberately inserted out of order: the ORDER BY is what the runner relies on.
        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PostExit, 0, "push", "git push", false, 600, true, cancellationToken);
        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PreLaunch, 1, "install", "npm ci", false, 600, true, cancellationToken);
        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PreLaunch, 0, "pull", "git pull", false, 600, true, cancellationToken);

        var names = new List<string>();
        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectStepsByEnvironmentId;
        select.Parameters.AddWithValue("$environmentId", environmentId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(4));
        }

        Assert.Equal(["pull", "install", "push"], names);
    }

    [Fact]
    public async Task EnabledSelect_FiltersDisabledStepsAndOtherPhases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenSchemaAsync(cancellationToken);
        var environmentId = await InsertEnvironmentAsync(connection, "Nightly", LLM.Claude, cancellationToken);

        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PreLaunch, 0, "on", "git pull", false, 600, true, cancellationToken);
        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PreLaunch, 1, "off", "npm ci", false, 600, false, cancellationToken);
        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PostExit, 0, "after", "git push", false, 600, true, cancellationToken);

        var names = new List<string>();
        await using var select = connection.CreateCommand();
        select.CommandText = SqlStrings.SelectEnabledStepsByEnvironmentIdAndPhase;
        select.Parameters.AddWithValue("$environmentId", environmentId);
        select.Parameters.AddWithValue("$phase", (int)EnvironmentStepPhase.PreLaunch);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(4));
        }

        Assert.Equal(["on"], names);
    }

    [Fact]
    public async Task DeletingAnEnvironment_CascadesItsSteps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenSchemaAsync(cancellationToken);
        var environmentId = await InsertEnvironmentAsync(connection, "Nightly", LLM.Claude, cancellationToken);
        var survivorId = await InsertEnvironmentAsync(connection, "Daily", LLM.Codex, cancellationToken);

        await InsertStepAsync(connection, environmentId, EnvironmentStepPhase.PreLaunch, 0, "pull", "git pull", false, 600, true, cancellationToken);
        await InsertStepAsync(connection, survivorId, EnvironmentStepPhase.PreLaunch, 0, "pull", "git pull", false, 600, true, cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = SqlStrings.DeleteEnvironment;
            delete.Parameters.AddWithValue("$id", environmentId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT EnvironmentId FROM EnvironmentSteps";
        await using var reader = await count.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(survivorId, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    [Fact]
    public async Task InsertingAStepForAMissingEnvironment_IsRejectedByTheForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenSchemaAsync(cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => InsertStepAsync(
            connection,
            environmentId: 4242,
            EnvironmentStepPhase.PreLaunch,
            0,
            "orphan",
            "git pull",
            false,
            600,
            true,
            cancellationToken));
    }

    [Fact]
    public async Task InitStatements_CreateTheTableOnAnAlreadyMigratedDatabase()
    {
        // The failure this guards against: shipping the table only for fresh databases, so every
        // existing state.db silently 500s on its first step read. Start from a legacy-shaped DB
        // (Environments already present, EnvironmentSteps absent) and replay exactly what
        // Repository.EnsureInitialized does.
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"viberails-legacy-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate";

        try
        {
            await using (var legacy = new SqliteConnection(connectionString))
            {
                await legacy.OpenAsync(cancellationToken);
                await using var create = legacy.CreateCommand();
                create.CommandText = SqlStrings.CreateEnvironmentsTable;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var sql in SqlStrings.InitStatements)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var check = connection.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'EnvironmentSteps'";
            Assert.Equal(1L, Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)));
        }
        finally
        {
            // Scoped to this test's connection string: a process-wide ClearAllPools() disposes
            // handles out from under DB test classes running in parallel.
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            try { File.Delete(databasePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LegacyIntKeyedTable_IsRebuiltToTheGuidShapeOnInit()
    {
        // EnvironmentSteps shipped briefly with an INTEGER AUTOINCREMENT Id. SQLite cannot ALTER
        // a PK type, so Repository.EnsureInitialized drops the old-shape table (deliberately
        // without porting rows — the feature had no adopters) before InitStatements recreates it
        // TEXT-keyed. This exercises the real Repository, guard included.
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetTempPath(), $"viberails-guid-steps-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate";

        try
        {
            await using (var legacy = new SqliteConnection(connectionString))
            {
                await legacy.OpenAsync(cancellationToken);
                await using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE EnvironmentSteps (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        EnvironmentId INTEGER NOT NULL,
                        Phase INTEGER NOT NULL,
                        Position INTEGER NOT NULL,
                        Name TEXT NOT NULL DEFAULT '',
                        Command TEXT NOT NULL,
                        StartMinimized INTEGER NOT NULL DEFAULT 0,
                        TimeoutSeconds INTEGER NOT NULL DEFAULT 600,
                        Enabled INTEGER NOT NULL DEFAULT 1,
                        CreatedUTC TEXT NOT NULL,
                        UpdatedUTC TEXT NOT NULL
                    );
                    INSERT INTO EnvironmentSteps
                        (EnvironmentId, Phase, Position, Name, Command, CreatedUTC, UpdatedUTC)
                    VALUES (1, 0, 0, 'old', 'git pull', '2026-01-01', '2026-01-01');
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            _ = new Repository(connectionString);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var idType = connection.CreateCommand())
            {
                idType.CommandText = "SELECT type FROM pragma_table_info('EnvironmentSteps') WHERE name = 'Id'";
                Assert.Equal("TEXT", Convert.ToString(await idType.ExecuteScalarAsync(cancellationToken)));
            }
            await using (var rowCount = connection.CreateCommand())
            {
                rowCount.CommandText = "SELECT COUNT(*) FROM EnvironmentSteps";
                Assert.Equal(0L, Convert.ToInt64(await rowCount.ExecuteScalarAsync(cancellationToken)));
            }

            // A second init must be a no-op — the guard keys on the Id column's type, and a
            // TEXT-keyed table holding rows has to survive every later boot. (This build's SQLite
            // enforces foreign keys on every connection, so the step needs a real parent row.)
            var environmentId = await InsertEnvironmentAsync(connection, "Survivor", LLM.Claude, cancellationToken);
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = SqlStrings.InsertEnvironmentStep;
                seed.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                seed.Parameters.AddWithValue("$environmentId", environmentId);
                seed.Parameters.AddWithValue("$phase", 0);
                seed.Parameters.AddWithValue("$position", 0);
                seed.Parameters.AddWithValue("$name", "keep");
                seed.Parameters.AddWithValue("$command", "git pull");
                seed.Parameters.AddWithValue("$startMinimized", 0);
                seed.Parameters.AddWithValue("$timeoutSeconds", 600);
                seed.Parameters.AddWithValue("$enabled", 1);
                seed.Parameters.AddWithValue("$createdUTC", DateTime.UtcNow.ToString("O"));
                seed.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("O"));
                await seed.ExecuteNonQueryAsync(cancellationToken);
            }
            // Force the next Repository to reopen rather than reuse a pooled handle. Scoped to this
            // test's connection string so parallel DB test classes keep their own pools.
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));

            _ = new Repository(connectionString);

            await using var recheck = connection.CreateCommand();
            recheck.CommandText = "SELECT COUNT(*) FROM EnvironmentSteps";
            Assert.Equal(1L, Convert.ToInt64(await recheck.ExecuteScalarAsync(cancellationToken)));
        }
        finally
        {
            // Scoped to this test's connection string: a process-wide ClearAllPools() disposes
            // handles out from under DB test classes running in parallel.
            SqliteConnection.ClearPool(new SqliteConnection(connectionString));
            try { File.Delete(databasePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CreateTable_IsRegisteredInInitStatementsNotMigrations()
    {
        // A brand-new CREATE TABLE IF NOT EXISTS is correct for fresh and legacy databases alike.
        // Putting it in MigrationStatements would mean an existing state.db only picks it up
        // through the log-and-skip path, which is where silent missing-table bugs come from.
        Assert.Contains(SqlStrings.CreateEnvironmentStepsTable, SqlStrings.InitStatements);
        Assert.Contains(SqlStrings.CreateEnvironmentStepsIndex, SqlStrings.InitStatements);
        Assert.DoesNotContain(SqlStrings.CreateEnvironmentStepsTable, SqlStrings.MigrationStatements);

        await Task.CompletedTask;
    }

    private static async Task<SqliteConnection> OpenSchemaAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        // foreign_keys is a per-connection pragma; the cascade under test depends on it.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = SqlStrings.PragmaForeignKeys;
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var sql in new[]
                 {
                     SqlStrings.CreateEnvironmentsTable,
                     SqlStrings.CreateEnvironmentStepsTable,
                     SqlStrings.CreateEnvironmentStepsIndex
                 })
        {
            await using var create = connection.CreateCommand();
            create.CommandText = sql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }

    private static async Task<int> InsertEnvironmentAsync(
        SqliteConnection connection,
        string customName,
        LLM llm,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = SqlStrings.InsertEnvironment;
        insert.Parameters.AddWithValue("$customName", customName);
        insert.Parameters.AddWithValue("$llm", (int)llm);
        insert.Parameters.AddWithValue("$path", "");
        insert.Parameters.AddWithValue("$customArgs", "");
        insert.Parameters.AddWithValue("$customPrompt", "");
        insert.Parameters.AddWithValue("$createdUTC", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$lastUsedUTC", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$hidden", 0);
        insert.Parameters.AddWithValue("$automationWorker", 0);
        insert.Parameters.AddWithValue("$workspaceMode", (int)EnvironmentWorkspaceMode.Project);
        insert.Parameters.AddWithValue("$projectPath", DBNull.Value);
        return Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertStepAsync(
        SqliteConnection connection,
        int environmentId,
        EnvironmentStepPhase phase,
        int position,
        string name,
        string command,
        bool startMinimized,
        int timeoutSeconds,
        bool enabled,
        CancellationToken cancellationToken,
        string? id = null)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = SqlStrings.InsertEnvironmentStep;
        insert.Parameters.AddWithValue("$id", id ?? Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$environmentId", environmentId);
        insert.Parameters.AddWithValue("$phase", (int)phase);
        insert.Parameters.AddWithValue("$position", position);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$command", command);
        insert.Parameters.AddWithValue("$startMinimized", startMinimized ? 1 : 0);
        insert.Parameters.AddWithValue("$timeoutSeconds", timeoutSeconds);
        insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$createdUTC", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}
