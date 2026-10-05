using Microsoft.Data.Sqlite;
using VibeRails.Services.BertV2;

namespace VibeRails.Data.Sqlite;

internal static class SearchDatabaseSchema
{
    internal static void Ensure(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        SqliteMigrationRunner.RequireGenerationAtMost(db, 1, "search");
        SqliteMigrationRunner.Apply(db, "shared-search", 1, MigrationKind.Additive, (connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE SearchDocuments (
                    Id TEXT PRIMARY KEY, Kind TEXT NOT NULL, ProjectPath TEXT NOT NULL,
                    SessionId TEXT, Metadata TEXT NOT NULL, SeenEpoch INTEGER NOT NULL);
                CREATE INDEX IX_SearchDocuments_Kind ON SearchDocuments(Kind, Id);
                CREATE INDEX IX_SearchDocuments_Session ON SearchDocuments(SessionId);
                CREATE TABLE SearchSources (
                    Id TEXT PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES SearchDocuments(Id) ON DELETE CASCADE,
                    SourceKey TEXT NOT NULL, Text TEXT NOT NULL, Title TEXT NOT NULL, Hash TEXT NOT NULL,
                    ChunkVersion TEXT, Attempts INTEGER NOT NULL DEFAULT 0, RetryAt INTEGER NOT NULL DEFAULT 0,
                    Lease TEXT, LeaseUntil INTEGER NOT NULL DEFAULT 0, Error TEXT);
                CREATE INDEX IX_SearchSources_Document ON SearchSources(DocumentId);
                CREATE INDEX IX_SearchSources_Work ON SearchSources(ChunkVersion, RetryAt, LeaseUntil);
                CREATE VIRTUAL TABLE SearchSources_fts USING fts5(Text, Title, content='SearchSources', content_rowid='rowid');
                CREATE TRIGGER SearchSources_ai AFTER INSERT ON SearchSources BEGIN
                    INSERT INTO SearchSources_fts(rowid,Text,Title) VALUES(new.rowid,new.Text,new.Title); END;
                CREATE TRIGGER SearchSources_ad AFTER DELETE ON SearchSources BEGIN
                    INSERT INTO SearchSources_fts(SearchSources_fts,rowid,Text,Title) VALUES('delete',old.rowid,old.Text,old.Title); END;
                CREATE TRIGGER SearchSources_au AFTER UPDATE OF Text,Title ON SearchSources BEGIN
                    INSERT INTO SearchSources_fts(SearchSources_fts,rowid,Text,Title) VALUES('delete',old.rowid,old.Text,old.Title);
                    INSERT INTO SearchSources_fts(rowid,Text,Title) VALUES(new.rowid,new.Text,new.Title); END;
                CREATE TABLE SearchChunks (
                    Id TEXT PRIMARY KEY, SourceId TEXT NOT NULL REFERENCES SearchSources(Id) ON DELETE CASCADE,
                    Position INTEGER NOT NULL, Text TEXT NOT NULL, Hash TEXT NOT NULL,
                    ModelVersion TEXT, Attempts INTEGER NOT NULL DEFAULT 0, RetryAt INTEGER NOT NULL DEFAULT 0,
                    Lease TEXT, LeaseUntil INTEGER NOT NULL DEFAULT 0, Error TEXT);
                CREATE INDEX IX_SearchChunks_Source ON SearchChunks(SourceId,Position);
                CREATE INDEX IX_SearchChunks_Work ON SearchChunks(ModelVersion,RetryAt,LeaseUntil);
                CREATE TABLE SearchCheckpoints (
                    Kind TEXT PRIMARY KEY, Cursor TEXT NOT NULL DEFAULT '', Epoch INTEGER NOT NULL DEFAULT 1,
                    Lease TEXT, LeaseUntil INTEGER NOT NULL DEFAULT 0, CompletedUtc TEXT,
                    Attempts INTEGER NOT NULL DEFAULT 0, RetryAt INTEGER NOT NULL DEFAULT 0, Error TEXT);
                """;
            cmd.ExecuteNonQuery();
        });
        SqliteMigrationRunner.StampGeneration(db, 1);
    }

    internal static void EnsureVectors(string path)
    {
        using var db = BertVectorDatabase.Open(path);
        SqliteMigrationRunner.Apply(db, "shared-search-vectors", 1, MigrationKind.Additive, (connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE VIRTUAL TABLE vec_search_chunks USING vec0(
                    Id TEXT PRIMARY KEY, Embedding FLOAT[384] distance_metric=cosine);
                """;
            cmd.ExecuteNonQuery();
        });
    }
}
