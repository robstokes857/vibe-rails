using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

namespace VibeRails.Data.Sqlite;

public sealed partial class SqliteSearchIndexStore
{
    public IReadOnlySet<string> GetBoardPrefixes(string projectPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BoardKeys.LegacyPrefix };
        if (!File.Exists(databasePath)) return result;
        using var db = Open(true);
        using var cmd = Command(db, $"SELECT DISTINCT p.value FROM SearchDocuments d,json_each(d.Metadata,'$.board.keyPrefixes') p WHERE {Eligibility}",
            Parameters("board", projectPath, null));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public BoardSearchDocument? FindSessionCard(string sessionId, string projectPath)
    {
        if (!File.Exists(databasePath)) return null;
        using var db = Open(true);
        using var cmd = Command(db, $"""
            SELECT d.Metadata FROM SearchDocuments d,json_each(d.Metadata,'$.board.sessions') s
            WHERE {Eligibility} AND json_extract(s.value,'$.sessionId')=$session ORDER BY d.rowid LIMIT 1
            """, Parameters("board", projectPath, null).Concat(new (string, object?)[] { ("$session", sessionId) }).ToArray());
        return cmd.ExecuteScalar() is string json ? Document(json).Board : null;
    }

    public IReadOnlyList<SearchDocument> ReadDocuments(string kind, int skip = 0, int take = 100,
        IReadOnlyCollection<string>? ids = null, string? sessionId = null, long? userInputId = null)
    {
        if (!File.Exists(databasePath)) return [];
        using var db = Open(true);
        using var cmd = Command(db, """
            SELECT d.Metadata,s.SourceKey,s.Text,s.Title FROM (
                SELECT rowid AS SortId,* FROM SearchDocuments WHERE Kind=$kind
                  AND ($ids IS NULL OR Id IN (SELECT value FROM json_each($ids)))
                  AND ($session IS NULL OR SessionId=$session)
                  AND ($input IS NULL OR json_extract(Metadata,'$.input.userInputId')=$input)
                ORDER BY rowid DESC LIMIT $take OFFSET $skip
            ) d JOIN SearchSources s ON s.DocumentId=d.Id ORDER BY d.SortId DESC,s.rowid
            """, ("$kind", kind), ("$ids", ids is null ? null : Strings(ids)), ("$session", sessionId),
            ("$take", Math.Clamp(take, 1, 10000)), ("$skip", Math.Max(0, skip)));
        cmd.Parameters.AddWithValue("$input", (object?)userInputId ?? DBNull.Value);
        var docs = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var doc = Document(reader.GetString(0));
            if (!docs.TryGetValue(doc.Id, out var existing)) docs[doc.Id] = existing = doc with { Sources = new List<SearchSource>() };
            ((List<SearchSource>)existing.Sources).Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return docs.Values.ToArray();
    }

    public IReadOnlyList<SearchMatch> ReadSessionChunks(int skip, int take, string? sessionId = null, int? chunk = null)
    {
        if (!File.Exists(databasePath)) return [];
        using var db = Open(true);
        using var cmd = Command(db, """
            SELECT d.Metadata,c.Text,c.Position FROM SearchDocuments d JOIN SearchSources s ON s.DocumentId=d.Id
            JOIN SearchChunks c ON c.SourceId=s.Id WHERE d.Kind='session' AND s.ChunkVersion=$version
                AND ($session IS NULL OR d.SessionId=$session) AND ($chunk IS NULL OR c.Position=$chunk)
            ORDER BY d.rowid DESC,c.Position LIMIT $take OFFSET $skip
            """, ("$version", SearchIndexVersions.Chunks), ("$session", sessionId), ("$chunk", chunk),
            ("$take", Math.Clamp(take, 1, 10000)), ("$skip", Math.Max(0, skip)));
        var result = new List<SearchMatch>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new(Document(reader.GetString(0)), reader.GetString(1), 0, 0, reader.GetInt32(2)));
        return result;
    }

    private static string Strings(IEnumerable<string> values) => System.Text.Json.JsonSerializer.Serialize(values.ToList(),
        VibeRails.DTOs.StorageJsonSerializerContext.Default.ListString);

    public IReadOnlyList<SearchMatch> Search(string kind, string query, float[]? embedding, int count,
        string? projectScope = null, IReadOnlyCollection<string>? excluded = null)
    {
        if (!File.Exists(databasePath)) return [];
        var matches = new Dictionary<string, SearchMatch>(StringComparer.Ordinal);
        using var db = Open(true);
        var words = Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(match => match.Value).Where(word => word.Length > 1 && !StopWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        var fts = words.Length == 0 ? "\"\"" : string.Join(" OR ", words.Select(word => "\"" + word.Replace("\"", "\"\"") + "\""));
        // Exact identities and literal substrings supplement FTS. Full sources preserve phrases that
        // cross model chunk boundaries. All eligibility filters precede ranking and limits.
        using var lexical = Command(db, $"""
            WITH candidates AS (
                SELECT d.Id,s.Id AS SourceId,c.Id AS ChunkId,coalesce(c.Position,0) AS Position,
                    CASE WHEN $query='' THEN 0 ELSE
                        3*(instr(lower(coalesce(c.Text,s.Text)),lower($query))>0)+
                        6*(instr(lower(s.Title),lower($query))>0)+
                        (SELECT count(*) FROM json_each($words) WHERE instr(lower(coalesce(c.Text,s.Text)),lower(value))>0)
                    END AS score,
                    CASE WHEN lower(d.Id)=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.key'))=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.displayId'))=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.keyPrefix')||'-'||json_extract(d.Metadata,'$.board.number'))=lower($query) OR
                        lower('VB-'||json_extract(d.Metadata,'$.board.number'))=lower($query) THEN 1 ELSE 0 END AS identityMatch
                FROM SearchDocuments d JOIN SearchSources s ON s.DocumentId=d.Id
                LEFT JOIN SearchChunks c ON d.Kind='session' AND c.SourceId=s.Id AND s.ChunkVersion=$chunks
                WHERE {Eligibility}
                  AND ($query='' OR instr(lower(coalesce(c.Text,s.Text)),lower($query))>0 OR d.Kind='board'
                    OR s.rowid IN (SELECT rowid FROM SearchSources_fts WHERE SearchSources_fts MATCH $fts))
            ), ranked AS (
                SELECT *,row_number() OVER(PARTITION BY Id ORDER BY score DESC,Position) AS rank FROM candidates
            ) SELECT d.Metadata,substr(coalesce(c.Text,s.Text),max(1,coalesce(
                    (SELECT min(nullif(instr(lower(coalesce(c.Text,s.Text)),lower(value)),0)) FROM json_each($words)),1)-70),520),score,ranked.Position
                FROM ranked JOIN SearchDocuments d ON d.Id=ranked.Id JOIN SearchSources s ON s.Id=ranked.SourceId
                LEFT JOIN SearchChunks c ON c.Id=ranked.ChunkId
                WHERE rank=1 AND ($query='' OR score>0 OR identityMatch=1)
                ORDER BY identityMatch DESC,score DESC,json_extract(d.Metadata,'$.board.updatedUtc') DESC,ranked.Id LIMIT $count
            """, Parameters(kind, projectScope, excluded).Concat(new (string, object?)[] {
                ("$query", query), ("$words", Strings(words)), ("$fts", fts), ("$count", count), ("$chunks", SearchIndexVersions.Chunks) }).ToArray());
        if (embedding is null || query.Length > 0)
        using (var reader = lexical.ExecuteReader())
            while (reader.Read())
            {
                var doc = Document(reader.GetString(0));
                matches[doc.Id] = new(doc, reader.GetString(1), reader.GetDouble(2), 0, reader.GetInt32(3));
            }
        if (embedding is not null)
        {
            try
            {
                using var vectors = BertVectorDatabase.Open(databasePath, readOnly: true);
                // Native sqlite-vec distance evaluation and SQL grouping avoid materializing vectors
                // or letting a long document consume the hit budget. Project filtering happens first.
                using var semantic = Command(vectors, $"""
                    WITH distances AS MATERIALIZED (
                        SELECT d.Id,c.Id AS ChunkId,c.Position,
                            1-vec_distance_cosine(v.Embedding,$vector) AS score
                        FROM SearchDocuments d JOIN SearchSources s ON s.DocumentId=d.Id
                        JOIN SearchChunks c ON c.SourceId=s.Id JOIN vec_search_chunks v ON v.Id=c.Id
                        WHERE {Eligibility} AND c.ModelVersion=$model AND s.ChunkVersion=$chunks
                    ), ranked AS (
                        SELECT *,row_number() OVER(PARTITION BY Id ORDER BY score DESC,Position) AS rank FROM distances
                    ) SELECT d.Metadata,c.Text,score,ranked.Position FROM ranked
                        JOIN SearchDocuments d ON d.Id=ranked.Id JOIN SearchChunks c ON c.Id=ranked.ChunkId
                        WHERE rank=1 AND score>=$threshold ORDER BY score DESC,ranked.Id LIMIT $count
                    """, Parameters(kind, projectScope, excluded).Concat(new (string, object?)[] {
                        ("$vector", BertVectorDatabase.Serialize(embedding)), ("$model", SearchIndexVersions.Model),
                        ("$chunks", SearchIndexVersions.Chunks), ("$threshold", kind == "board" ? .45 : -1), ("$count", count) }).ToArray());
                using var reader = semantic.ExecuteReader();
                while (reader.Read())
                {
                    var doc = Document(reader.GetString(0));
                    if (matches.TryGetValue(doc.Id, out var existing))
                        matches[doc.Id] = existing with { Semantic = reader.GetDouble(2) };
                    else matches[doc.Id] = new(doc, reader.GetString(1), 0, reader.GetDouble(2), reader.GetInt32(3));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Serilog.Log.Warning(ex, "Search vectors unavailable; retaining keyword matches"); }
        }
        return matches.Values.ToArray();
    }

    private static string Eligibility => """
        d.Kind=$kind AND ($scope IS NULL OR d.ProjectPath=$scope COLLATE {COLLATION})
        AND ($excluded IS NULL OR d.Id NOT IN (SELECT value FROM json_each($excluded)))
        """.Replace("{COLLATION}", OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "NOCASE" : "BINARY");

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "card", "cards", "where", "what", "that", "this", "about", "with", "did", "was", "for", "how", "have", "from", "and", "are", "can", "to", "of" };

    private static (string, object?)[] Parameters(string kind, string? projectScope, IReadOnlyCollection<string>? excluded) =>
        [("$kind", kind), ("$scope", projectScope is null ? null : BoardPaths.NormalizeProjectPath(projectScope)),
         ("$excluded", excluded is null ? null : Strings(excluded))];
}
