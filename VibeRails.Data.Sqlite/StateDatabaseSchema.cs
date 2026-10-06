using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VibeRails.DB;
using VibeRails.Services.UserInOut;

namespace VibeRails.Data.Sqlite;

/// <summary>Adopts existing state.db files once, then applies only new numbered migrations.</summary>
internal static class StateDatabaseSchema
{
    /// <summary>
    /// The state.db schema generation this build writes, stored in PRAGMA user_version. Bumped only
    /// by breaking migrations (state/2 search index: generation 2; board/8 history retirement: 3). A build refuses a
    /// database above its own generation; additive changes never move it. Values stay at or above 1
    /// because the shipped 1.10.10 binary reads user_version &lt; 1 as "rebuild the FTS table".
    /// </summary>
    internal const int Generation = 3;

    internal static void Ensure(string connectionString, ILogger? logger = null)
    {
        using var connection = SqliteConnectionFactory.Open(connectionString);
        SqliteConnectionFactory.EnsureWalMode(connection);
        SqliteMigrationRunner.RequireGenerationAtMost(connection, Generation, "state.db");
        SqliteMigrationRunner.Apply(connection, "state", 1, MigrationKind.Additive, (db, transaction) =>
        {
            using (var steps = db.CreateCommand())
            {
                steps.Transaction = transaction;
                steps.CommandText = "SELECT type FROM pragma_table_info('EnvironmentSteps') WHERE name='Id';";
                if (steps.ExecuteScalar() is string type && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
                    SqliteSchema.Execute(db, transaction, "DROP TABLE EnvironmentSteps;");
            }
            foreach (var sql in SqlStrings.InitStatements)
                SqliteSchema.Execute(db, transaction, sql);
            foreach (var sql in SqlStrings.MigrationStatements)
            {
                if (SqliteSchema.AdoptStatement(db, transaction, sql) && sql == SqlStrings.AddProcessedColumn)
                    SqliteSchema.Execute(db, transaction, SqlStrings.SeedProcessedColumn);
            }

        });
        SqliteMigrationRunner.Apply(connection, "environment-purpose", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.MigrateEnvironmentsAddPurpose));
        SqliteMigrationRunner.Apply(connection, "environment-review-routing", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE Environments ADD COLUMN ReviewerRoutingJson TEXT"));
        var queued = 0;
        // Breaking: 1.10.10 writes UserInputs_fts directly over UserInputs; after this the index is
        // fed from UserInputSearchDocuments. Existing databases upgrade automatically with a
        // pre-migration backup, just like every other breaking migration.
        SqliteMigrationRunner.Apply(connection, "state", 2, MigrationKind.Breaking, (db, transaction) =>
        {
            if (SqliteSchema.HasColumn(db, transaction, "UserInputs", "CleanedId"))
                SqliteSchema.Execute(db, transaction,
                    "CREATE INDEX IF NOT EXISTS idx_user_inputs_cleaned_id ON UserInputs(CleanedId);");
            SqliteSchema.Execute(db, transaction, """
                CREATE INDEX IF NOT EXISTS idx_file_changes_previous_input ON InputFileChanges(PreviousInputId);
                CREATE INDEX IF NOT EXISTS idx_sessions_retention ON Sessions(EndedUTC,Id)
                    WHERE EndedUTC IS NOT NULL AND ExportedUTC IS NOT NULL;
                CREATE TABLE IF NOT EXISTS UserInputSearchPending (
                    UserInputId INTEGER PRIMARY KEY REFERENCES UserInputs(Id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS UserInputSearchDocuments (
                    UserInputId INTEGER PRIMARY KEY REFERENCES UserInputs(Id) ON DELETE CASCADE,
                    InputText TEXT NOT NULL
                );
                DROP TRIGGER IF EXISTS UserInputs_fts_ad;
                DROP TABLE IF EXISTS UserInputs_fts;
                CREATE VIRTUAL TABLE UserInputs_fts USING fts5(
                    InputText, content='UserInputSearchDocuments', content_rowid='UserInputId',
                    tokenize='porter unicode61'
                );
                CREATE TRIGGER UserInputSearchDocuments_ai AFTER INSERT ON UserInputSearchDocuments BEGIN
                    INSERT INTO UserInputs_fts(rowid, InputText) VALUES(new.UserInputId, new.InputText);
                END;
                CREATE TRIGGER UserInputSearchDocuments_ad AFTER DELETE ON UserInputSearchDocuments BEGIN
                    INSERT INTO UserInputs_fts(UserInputs_fts,rowid,InputText) VALUES('delete',old.UserInputId,old.InputText);
                END;
                CREATE TRIGGER UserInputSearchDocuments_au AFTER UPDATE ON UserInputSearchDocuments BEGIN
                    INSERT INTO UserInputs_fts(UserInputs_fts,rowid,InputText) VALUES('delete',old.UserInputId,old.InputText);
                    INSERT INTO UserInputs_fts(rowid,InputText) VALUES(new.UserInputId,new.InputText);
                END;
                CREATE TRIGGER UserInputs_fts_ad AFTER DELETE ON UserInputs BEGIN
                    DELETE FROM UserInputSearchDocuments WHERE UserInputId=old.Id;
                    DELETE FROM UserInputSearchPending WHERE UserInputId=old.Id;
                END;
                CREATE TRIGGER UserInputs_search_ai AFTER INSERT ON UserInputs BEGIN
                    INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) VALUES(new.Id);
                END;
                CREATE TRIGGER UserInputs_search_au AFTER UPDATE OF InputText ON UserInputs
                WHEN old.InputText IS NOT new.InputText BEGIN
                    DELETE FROM UserInputSearchDocuments WHERE UserInputId=old.Id;
                    INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) VALUES(new.Id);
                END;
                """);
            // Queue the rebuild instead of performing it here. The old external-content index
            // referred to raw text, so deleting an ETL-skipped or normalized prompt could corrupt
            // its inverted index -- every row therefore has to be re-derived through
            // InputEtlFilter. Doing that inline held this BEGIN IMMEDIATE transaction open for the
            // whole scan while every other vb process kept only its ordinary 5s busy timeout, so on
            // a large database the one-time upgrade produced spurious SQLITE_BUSY failures
            // elsewhere. Seeding the queue is one set-based INSERT; the drain below and
            // SearchIndexMaintenanceJob both go through SearchIndexWriter in bounded batches.
            using (var seed = db.CreateCommand())
            {
                seed.Transaction = transaction;
                seed.CommandText = "INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) SELECT Id FROM UserInputs;";
                queued = seed.ExecuteNonQuery();
            }
            using var version = db.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar()) < 1)
                SqliteSchema.Execute(db, transaction, "PRAGMA user_version=1;");
            logger?.LogInformation("Adopted state database search schema; queued {Count} prompts for indexing.", queued);
        });
        // Legacy search tables/triggers remain for older binaries. Current indexing and replay
        // are owned by search.db; startup never drains or rebuilds the retired state index.
        SqliteMigrationRunner.Apply(connection, "state", 3, MigrationKind.Additive, (db, transaction) =>
        {
            // Retention's proof that a session's proxy exchanges were actually backed up.
            // ExportedUTC alone is not that proof: schema-v1 envelopes carry no proxy data, frozen
            // v1 retries stay v1, and a v2 envelope whose proxy read failed is still acknowledged
            // with coverage "unavailable". Existing rows stay NULL and are therefore never pruned,
            // which is the safe direction -- disk is recoverable, deleted exchanges are not.
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.MigrateSessionsAddExportedProxyCoverage);
        });
        SqliteMigrationRunner.Apply(connection, "state", 4, MigrationKind.Additive, (db, transaction) =>
        {
            // The boundary of that proof. The proxy queues an exchange stamped with its CreatedUTC
            // and writes it later, so a row can land after the export snapshot was taken while
            // carrying a timestamp from before it. The snapshot's largest rowid is what separates
            // rows the acknowledged envelope contains from rows it does not. Sessions acknowledged
            // before this column existed stay NULL and are therefore never pruned.
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.MigrateSessionsAddExportedProxyMaxRowId);
        });
        SqliteMigrationRunner.Apply(connection, "search-component-writer", 1, MigrationKind.Additive, (db, transaction) =>
        {
            // Only new writes opt in. Existing records and older binaries retain their legacy
            // queue behavior; no stored text, search rows or completion marks are converted.
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE UserInputs ADD COLUMN SearchComponent TEXT");
            SqliteSchema.Execute(db, transaction, """
                DROP TRIGGER UserInputs_search_ai;
                CREATE TRIGGER UserInputs_search_ai AFTER INSERT ON UserInputs
                WHEN new.SearchComponent IS NULL BEGIN
                    INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) VALUES(new.Id);
                END;
                DROP TRIGGER UserInputs_search_au;
                CREATE TRIGGER UserInputs_search_au AFTER UPDATE OF InputText ON UserInputs
                WHEN old.InputText IS NOT new.InputText AND new.SearchComponent IS NULL BEGIN
                    DELETE FROM UserInputSearchDocuments WHERE UserInputId=old.Id;
                    INSERT OR IGNORE INTO UserInputSearchPending(UserInputId) VALUES(new.Id);
                END;
                """);
        });
        SqliteMigrationRunner.Apply(connection, "session-sharing", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, """
                CREATE TABLE SessionShareUploads (
                    SessionId TEXT NOT NULL REFERENCES Sessions(Id) ON DELETE CASCADE,
                    KeyFingerprint TEXT NOT NULL,
                    RequestedUTC TEXT NOT NULL,
                    CompletedUTC TEXT NULL,
                    PRIMARY KEY (SessionId, KeyFingerprint)
                );
                CREATE INDEX idx_session_share_uploads_pending
                    ON SessionShareUploads(KeyFingerprint, RequestedUTC) WHERE CompletedUTC IS NULL;
                """));
        JobStore.EnsureSessionLinkSchema(connection);
        // Every breaking step above has now been applied (or was already), so the file is at this
        // build's generation. Stamping after the fact also covers databases migrated before the
        // generation existed.
        SqliteMigrationRunner.StampGeneration(connection, Generation);
    }

}
