using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;

namespace VibeRails.Data.Sqlite;

/// <summary>Shared search storage. No query method opens a canonical source database or writes work.</summary>
public sealed partial class SqliteSearchIndexStore(string databasePath, string statePath) : ISearchIndexStore
{
    public string DatabasePath => databasePath;
    public string StateDatabasePath => statePath;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private const long LeaseMilliseconds = 120_000;
    private readonly Lock _schemaLock = new();
    private bool _initialized;

    private void Initialize()
    {
        lock (_schemaLock)
        {
            if (_initialized && File.Exists(databasePath)) return;
            SearchDatabaseSchema.Ensure(databasePath);
            _initialized = true;
        }
    }

    private SqliteConnection Open(bool readOnly = false) => SqliteConnectionFactory.Open(
        new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString(), readOnly);

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static int Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(db, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Json(SearchDocument document) => JsonSerializer.Serialize(document, StorageJsonSerializerContext.Default.SearchDocument);
    private static SearchDocument Document(string json) => JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.SearchDocument)!;

    public IReadOnlyList<SearchWork> ClaimSources(string kind, int count, string version) => Claim(kind, count, version, true);
    public IReadOnlyList<SearchWork> ClaimChunks(string kind, int count, string modelVersion) => Claim(kind, count, modelVersion, false);

    private IReadOnlyList<SearchWork> Claim(string kind, int count, string version, bool source)
    {
        Initialize();
        using var db = Open();
        using var tx = db.BeginTransaction();
        var table = source ? "SearchSources" : "SearchChunks";
        var versionColumn = source ? "ChunkVersion" : "ModelVersion";
        using var read = Command(db, $"""
            SELECT w.Id,w.Text,{(source ? "w.Title" : "''")},w.Hash
            FROM {table} w
            {(source ? "" : "JOIN SearchSources s ON s.Id=w.SourceId")}
            JOIN SearchDocuments d ON d.Id={(source ? "w" : "s")}.DocumentId
            WHERE d.Kind=$kind AND (w.{versionColumn} IS NULL OR w.{versionColumn}<>$version)
                AND w.LeaseUntil<=$now AND w.RetryAt<=$now
                {(source ? "" : "AND s.ChunkVersion=$chunkVersion")}
            ORDER BY w.Attempts,w.Id LIMIT $count
            """, ("$kind", kind), ("$version", version), ("$now", Now), ("$count", Math.Clamp(count, 1, 100)),
            ("$chunkVersion", SearchIndexVersions.Chunks));
        var rows = new List<SearchWork>();
        using (var reader = read.ExecuteReader())
            while (reader.Read()) rows.Add(new(reader.GetString(0), kind, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Guid.NewGuid().ToString("N")));
        foreach (var row in rows)
            Execute(db, $"UPDATE {table} SET Lease=$lease,LeaseUntil=$until WHERE Id=$id",
                ("$lease", row.Lease), ("$until", Now + LeaseMilliseconds), ("$id", row.Id));
        tx.Commit();
        return rows;
    }

    public bool Renew(SearchWork work, bool source)
    {
        using var db = Open();
        return Execute(db, $"UPDATE {(source ? "SearchSources" : "SearchChunks")} SET LeaseUntil=$until WHERE Id=$id AND Hash=$hash AND Lease=$lease AND LeaseUntil>$now",
            ("$until", Now + LeaseMilliseconds), ("$id", work.Id), ("$hash", work.Hash), ("$lease", work.Lease), ("$now", Now)) == 1;
    }

    public void CompleteSource(SearchWork work, IReadOnlyList<string> chunks, string version)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (Execute(db, """
            UPDATE SearchSources SET ChunkVersion=$version,Attempts=0,RetryAt=0,Error=NULL,Lease=NULL,LeaseUntil=0
            WHERE Id=$id AND Hash=$hash AND Lease=$lease AND LeaseUntil>$now
            """, ("$version", version), ("$id", work.Id), ("$hash", work.Hash), ("$lease", work.Lease), ("$now", Now)) != 1) return;
        Execute(db, "DELETE FROM SearchChunks WHERE SourceId=$id", ("$id", work.Id));
        for (var i = 0; i < chunks.Count; i++)
            Execute(db, "INSERT INTO SearchChunks(Id,SourceId,Position,Text,Hash) VALUES($id,$source,$position,$text,$hash)",
                ("$id", work.Id + ":" + i), ("$source", work.Id), ("$position", i), ("$text", chunks[i]),
                ("$hash", Hash(version + "\n" + chunks[i])));
        if (work.Kind == "session")
            Execute(db, """
                UPDATE SearchDocuments SET Metadata=json_set(Metadata,'$.session.totalChunkCount',$count)
                WHERE Id=(SELECT DocumentId FROM SearchSources WHERE Id=$id)
                """, ("$count", chunks.Count), ("$id", work.Id));
        tx.Commit();
    }

    public void CompleteChunk(SearchWork work, float[] vector, string modelVersion)
    {
        var bytes = BertVectorDatabase.Serialize(vector);
        if (vector.Any(value => !float.IsFinite(value))) throw new ArgumentException("Embedding contains non-finite values.");
        SearchDatabaseSchema.EnsureVectors(databasePath);
        using var db = BertVectorDatabase.Open(databasePath);
        using var tx = db.BeginTransaction();
        if (Execute(db, """
            UPDATE SearchChunks SET ModelVersion=$version,Attempts=0,RetryAt=0,Error=NULL,Lease=NULL,LeaseUntil=0
            WHERE Id=$id AND Hash=$hash AND Lease=$lease AND LeaseUntil>$now
            """, ("$version", modelVersion), ("$id", work.Id), ("$hash", work.Hash), ("$lease", work.Lease), ("$now", Now)) != 1) return;
        Execute(db, "DELETE FROM vec_search_chunks WHERE Id=$id", ("$id", work.Id));
        Execute(db, "INSERT INTO vec_search_chunks(Id,Embedding) VALUES($id,$vector)", ("$id", work.Id), ("$vector", bytes));
        tx.Commit();
    }

    public void Fail(SearchWork work, bool source, string error)
    {
        using var db = Open();
        Execute(db, $"""
            UPDATE {(source ? "SearchSources" : "SearchChunks")}
            SET Attempts=Attempts+1,RetryAt=$now+min(3600000,30000*(1<<min(Attempts,7))),
                Error=$error,Lease=NULL,LeaseUntil=0
            WHERE Id=$id AND Hash=$hash AND Lease=$lease
            """, ("$now", Now), ("$error", error[..Math.Min(500, error.Length)]),
            ("$id", work.Id), ("$hash", work.Hash), ("$lease", work.Lease));
    }

    public void Release(SearchWork work, bool source)
    {
        using var db = Open();
        Execute(db, $"UPDATE {(source ? "SearchSources" : "SearchChunks")} SET Lease=NULL,LeaseUntil=0 WHERE Id=$id AND Lease=$lease",
            ("$id", work.Id), ("$lease", work.Lease));
    }

    public SearchIndexProgress GetProgress()
    {
        if (!File.Exists(databasePath)) return new(0, 0, 0, 0, 0, 0, null, null);
        using var db = Open(true);
        using var cmd = Command(db, """
            SELECT (SELECT count(*) FROM SearchDocuments), (SELECT count(*) FROM SearchSources),
              (SELECT count(*) FROM SearchChunks), (SELECT count(*) FROM SearchChunks WHERE ModelVersion=$model),
              (SELECT count(*) FROM SearchSources WHERE ChunkVersion IS NULL OR ChunkVersion<>$chunks),
              (SELECT count(*) FROM SearchSources WHERE Error IS NOT NULL)+(SELECT count(*) FROM SearchChunks WHERE Error IS NOT NULL)
                  +(SELECT count(*) FROM SearchCheckpoints WHERE Error IS NOT NULL),
              (SELECT Error FROM (SELECT Error,RetryAt FROM SearchSources UNION ALL SELECT Error,RetryAt FROM SearchChunks UNION ALL SELECT Error,RetryAt FROM SearchCheckpoints)
                  WHERE Error IS NOT NULL ORDER BY RetryAt DESC LIMIT 1),
              (SELECT max(CompletedUtc) FROM SearchCheckpoints)
            """, ("$model", SearchIndexVersions.Model), ("$chunks", SearchIndexVersions.Chunks));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public int Count(string kind, bool vectors = false)
    {
        if (!File.Exists(databasePath)) return 0;
        using var db = Open(true);
        if (kind == "history-sessions")
        {
            using var sessions = Command(db, "SELECT count(DISTINCT SessionId) FROM SearchDocuments WHERE Kind='input'");
            return Convert.ToInt32(sessions.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (kind == "session-chunks")
        {
            using var chunks = Command(db, "SELECT count(*) FROM SearchChunks c JOIN SearchSources s ON s.Id=c.SourceId JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind='session'");
            return Convert.ToInt32(chunks.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var cmd = Command(db, vectors ? """
            SELECT count(*) FROM SearchChunks c JOIN SearchSources s ON s.Id=c.SourceId
            JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind=$kind AND c.ModelVersion=$model
            """ : "SELECT count(*) FROM SearchDocuments WHERE Kind=$kind", ("$kind", kind), ("$model", SearchIndexVersions.Model));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public bool Repair()
    {
        Initialize();
        using var db = Open();
        var repaired = false;
        try { Execute(db, "INSERT INTO SearchSources_fts(SearchSources_fts,rank) VALUES('integrity-check',1)"); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11)
        {
            Execute(db, "INSERT INTO SearchSources_fts(SearchSources_fts) VALUES('rebuild')");
            repaired = true;
        }
        SearchDatabaseSchema.EnsureVectors(databasePath);
        using var vectors = BertVectorDatabase.Open(databasePath);
        using var tx = vectors.BeginTransaction();
        repaired |= Execute(vectors, """
            UPDATE SearchChunks SET ModelVersion=NULL WHERE Id IN (
                SELECT Id FROM SearchChunks WHERE ModelVersion IS NOT NULL
                    AND Id NOT IN (SELECT Id FROM vec_search_chunks) LIMIT 256)
            """) > 0;
        repaired |= Execute(vectors, """
            DELETE FROM vec_search_chunks WHERE Id IN (
                SELECT Id FROM vec_search_chunks WHERE Id NOT IN (SELECT Id FROM SearchChunks) LIMIT 256)
            """) > 0;
        tx.Commit();
        return repaired;
    }
}
