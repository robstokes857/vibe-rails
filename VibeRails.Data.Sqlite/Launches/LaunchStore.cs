using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;

namespace VibeRails.Data.Sqlite;

/// <summary>A separate component database. No legacy environment, Job or recording rows are adopted or changed.</summary>
public sealed partial class LaunchStore : ILaunchStore
{
    private readonly string connectionString;
    public const int Generation = 1;

    public LaunchStore(string connectionString)
    {
        // One short-lived connection per operation; do not retain file handles after component teardown.
        this.connectionString = new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
        using var db = Open();
        SqliteMigrationRunner.RequireGenerationAtMost(db, Generation, "launches");
        SqliteMigrationRunner.Apply(db, "launches", 1, MigrationKind.Additive, (migration, tx) =>
        {
            using var command = migration.CreateCommand();
            command.Transaction = tx;
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        });
        SqliteMigrationRunner.Apply(db, "launches", 2, MigrationKind.Additive, (migration, tx) =>
        {
            using var command = migration.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "ALTER TABLE Runs ADD COLUMN ExecutionPid INTEGER; ALTER TABLE Runs ADD COLUMN ExecutionStartedUTC TEXT;";
            command.ExecuteNonQuery();
        });
        // VIBE-296: the `project:<key>` lease names an upper-cased comparison key. Open-root readers
        // need the project as its root spelled it. A separate table, not a RuntimeLeases column:
        // older builds INSERT into RuntimeLeases positionally and would fail on a fourth column.
        SqliteMigrationRunner.Apply(db, "launches", 3, MigrationKind.Additive, (migration, tx) =>
        {
            using var command = migration.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "CREATE TABLE ProjectRootPaths (ProjectKey TEXT PRIMARY KEY, ProjectRoot TEXT NOT NULL);";
            command.ExecuteNonQuery();
        });
        SqliteMigrationRunner.Apply(db, "launches", 4, MigrationKind.Additive, (migration, tx) =>
        {
            using var command = migration.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                CREATE TABLE DefinitionProjects (
                    DefinitionType TEXT NOT NULL, DefinitionId TEXT NOT NULL, ProjectKey TEXT NOT NULL,
                    PRIMARY KEY (DefinitionType, DefinitionId, ProjectKey));
                """;
            command.ExecuteNonQuery();
        });
        // VIBE-321: a card's launch runs (GetBoardRunsAsync) read json_extract over every run of the
        // project; this expression index answers them directly (289 ms -> 0.1 ms at 10,000 runs).
        SqliteMigrationRunner.Apply(db, "launches", 5, MigrationKind.Additive, (migration, tx) =>
        {
            using var command = migration.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "CREATE INDEX IX_LaunchRuns_Card ON Runs(ProjectPath, json_extract(Snapshot,'$.context.cardId'), Sequence DESC);";
            command.ExecuteNonQuery();
        });
        SqliteConnectionFactory.EnsureWalMode(db);
    }

    private SqliteConnection Open() => SqliteConnectionFactory.Open(connectionString);
    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Date(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private const string Schema = """
        CREATE TABLE LaunchPresets (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL,
            Cli INTEGER NOT NULL, InitialPrompt TEXT, Enabled INTEGER NOT NULL,
            Revision INTEGER NOT NULL, CreatedUTC TEXT NOT NULL, UpdatedUTC TEXT NOT NULL,
            RetiredUTC TEXT, Payload TEXT NOT NULL);
        CREATE TABLE Actions (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL, Kind TEXT NOT NULL,
            Enabled INTEGER NOT NULL, Revision INTEGER NOT NULL, CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL, RetiredUTC TEXT, Payload TEXT NOT NULL);
        CREATE TABLE TriggerBindings (
            Id TEXT PRIMARY KEY, DefinitionId TEXT NOT NULL, DefinitionType TEXT NOT NULL,
            ProjectPath TEXT NOT NULL, Enabled INTEGER NOT NULL, Revision INTEGER NOT NULL,
            NextRunUTC TEXT, Payload TEXT NOT NULL);
        CREATE INDEX IX_LaunchBindings_Due ON TriggerBindings(Enabled, NextRunUTC);
        CREATE INDEX IX_LaunchBindings_Definition ON TriggerBindings(DefinitionId, ProjectPath);
        CREATE TABLE ScriptApprovals (
            ProjectPath TEXT NOT NULL, DefinitionId TEXT NOT NULL, ContentHash TEXT NOT NULL,
            ApprovedUTC TEXT NOT NULL, PRIMARY KEY(ProjectPath, DefinitionId, ContentHash));
        CREATE TABLE Runs (
            Sequence INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE,
            DefinitionId TEXT NOT NULL, DefinitionType TEXT NOT NULL, ProjectPath TEXT NOT NULL,
            OccurrenceId TEXT NOT NULL, Snapshot TEXT NOT NULL, Status TEXT NOT NULL,
            QueuedUTC TEXT NOT NULL, SourceRunId TEXT, ClaimToken TEXT, OwnerPid INTEGER,
            OwnerStartedUTC TEXT, ClaimedUTC TEXT, StartedUTC TEXT, EndedUTC TEXT,
            CancelRequested INTEGER NOT NULL DEFAULT 0, SessionId TEXT, TabId TEXT,
            ResolvedPrompt TEXT, ResolvedWorkingDirectory TEXT, ExitCode INTEGER, Error TEXT,
            EvidenceId TEXT, Verdict TEXT, DeadlineUTC TEXT,
            UNIQUE(ProjectPath, OccurrenceId));
        CREATE INDEX IX_LaunchRuns_Active ON Runs(Status, Sequence);
        CREATE INDEX IX_LaunchRuns_History ON Runs(ProjectPath, DefinitionId, Sequence DESC);
        CREATE TABLE RuntimeLeases (Name TEXT PRIMARY KEY, Owner TEXT NOT NULL, UntilUTC TEXT NOT NULL);
        CREATE TABLE ActionEvidence (RunId TEXT PRIMARY KEY REFERENCES Runs(Id), Payload TEXT NOT NULL);
        CREATE TABLE DefinitionAttachments (
            Id TEXT PRIMARY KEY, DefinitionId TEXT NOT NULL REFERENCES LaunchPresets(Id),
            Name TEXT NOT NULL, MimeType TEXT NOT NULL, Size INTEGER NOT NULL,
            CreatedUTC TEXT NOT NULL, RemovedUTC TEXT, Content BLOB NOT NULL);
        CREATE INDEX IX_LaunchAttachments_Owner ON DefinitionAttachments(DefinitionId, RemovedUTC);
        PRAGMA user_version=1;
        """;
}
