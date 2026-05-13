using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Utils;

namespace VibeRails.Services.BertV2;

public sealed class BertSearchDbService : IBertSearchDbService
{
    private readonly string _vectorDatabasePath;
    private readonly string _fallbackStateDatabasePath;
    private readonly string? _stateDatabasePathOverride;

    public BertSearchDbService(IBertSettings settings)
        : this(settings, stateDatabasePathOverride: null)
    {
    }

    /// <summary>
    /// Test-friendly overload: pass an explicit state.db path to avoid resolving via
    /// the global ParserConfigs/install-dir fallback. Production should not use this.
    /// </summary>
    public BertSearchDbService(IBertSettings settings, string? stateDatabasePathOverride)
    {
        _vectorDatabasePath = Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName);
        _fallbackStateDatabasePath = Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.STATE_FILENAME);
        _stateDatabasePathOverride = stateDatabasePathOverride;
    }

    public string VectorDatabasePath => _vectorDatabasePath;

    public string StateDatabasePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_stateDatabasePathOverride))
                return _stateDatabasePathOverride;
            var configured = ParserConfigs.GetStatePath();
            return string.IsNullOrWhiteSpace(configured) ? _fallbackStateDatabasePath : configured;
        }
    }

    public bool VectorDatabaseExists => File.Exists(_vectorDatabasePath);

    public int CountDocuments()
    {
        if (!VectorDatabaseExists) return 0;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BertSearchSchema.DocumentTableName};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountSessions()
    {
        if (!VectorDatabaseExists) return 0;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(DISTINCT substr(Id, 1, instr(Id, ':') - 1))
            FROM {BertSearchSchema.DocumentTableName}
            WHERE instr(Id, ':') > 0;
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountVectors()
    {
        if (!VectorDatabaseExists) return 0;
        using var connection = OpenVectorConnection(loadVectorExtension: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BertSearchSchema.VectorTableName};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountSessionDocuments()
    {
        if (!VectorDatabaseExists) return 0;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        if (!TableExists(connection, BertSearchSchema.SessionDocumentTableName)) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BertSearchSchema.SessionDocumentTableName};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountSessionVectors()
    {
        if (!VectorDatabaseExists) return 0;
        using var connection = OpenVectorConnection(loadVectorExtension: true);
        if (!TableExists(connection, BertSearchSchema.SessionVectorTableName)) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BertSearchSchema.SessionVectorTableName};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public string? GetLatestDocumentId()
    {
        if (!VectorDatabaseExists) return null;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id
            FROM {BertSearchSchema.DocumentTableName}
            ORDER BY rowid DESC
            LIMIT 1;
            """;
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<BertStoredDocument> GetCaptures(int skip, int take)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.DocumentTableName}
            ORDER BY rowid DESC
            LIMIT @take OFFSET @skip;
            """;
        command.Parameters.AddWithValue("@take", take);
        command.Parameters.AddWithValue("@skip", skip);

        return ReadDocuments(command, includeScore: false);
    }

    public IReadOnlyList<BertStoredDocument> GetCapturesBySessionId(string sessionId)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.DocumentTableName}
            WHERE Id LIKE @prefix || ':%'
            ORDER BY rowid DESC;
            """;
        command.Parameters.AddWithValue("@prefix", sessionId);

        return ReadDocuments(command, includeScore: false);
    }

    public IReadOnlyList<BertStoredDocument> GetSessionCaptures(int skip, int take)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        if (!TableExists(connection, BertSearchSchema.SessionDocumentTableName)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.SessionDocumentTableName}
            ORDER BY rowid DESC
            LIMIT @take OFFSET @skip;
            """;
        command.Parameters.AddWithValue("@take", take);
        command.Parameters.AddWithValue("@skip", skip);

        return ReadDocuments(command, includeScore: false);
    }

    public BertStoredDocument? GetCapture(string documentId)
    {
        if (!VectorDatabaseExists) return null;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.DocumentTableName}
            WHERE Id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", documentId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new BertStoredDocument(
            reader.GetString(0),
            reader.GetString(1),
            null);
    }

    public IReadOnlyList<BertStoredDocument> SearchByText(string query, int topK)
    {
        // Empty queries must not reach the LIKE fallback — '%' || '' || '%' matches
        // every row.
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<BertStoredDocument>();

        // Prefer FTS5 over UserInputs.InputText (state.db) — much better tokenization
        // and ranking than substring LIKE. Only fall back to LIKE when FTS5 is
        // *unavailable* (no state.db, no FTS5 build, missing table). FTS5 running
        // and returning no hits is the authoritative answer; falling back to LIKE
        // there silently switches to a different corpus with different ranking.
        var fts5 = TrySearchByFts5(query, topK);
        if (fts5 is not null)
            return fts5;

        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.DocumentTableName}
            WHERE Text LIKE '%' || @query || '%'
            ORDER BY rowid DESC
            LIMIT @topK;
            """;
        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@topK", topK);

        return ReadDocuments(command, includeScore: false);
    }

    /// <summary>
    /// Runs the FTS5 query. Returns the result list (possibly empty) when FTS5 ran,
    /// or null when FTS5 is unavailable and the caller should fall back to LIKE.
    /// </summary>
    private IReadOnlyList<BertStoredDocument>? TrySearchByFts5(string query, int topK)
    {
        if (!File.Exists(StateDatabasePath))
            return null;

        SqliteConnection connection;
        try
        {
            connection = OpenStateConnection();
        }
        catch (SqliteException)
        {
            return null;
        }

        using (connection)
        {
            if (!TableExists(connection, "UserInputs_fts"))
                return null;

            using var command = connection.CreateCommand();
            command.CommandText = SqlStrings.SelectUserInputsFtsByQuery;
            command.Parameters.AddWithValue("$query", BuildFts5MatchExpression(query));
            command.Parameters.AddWithValue("$topK", topK);

            var results = new List<BertStoredDocument>();
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var sessionId = reader.GetString(0);
                    var userInputId = reader.GetInt64(1);
                    var inputText = reader.GetString(2);
                    results.Add(new BertStoredDocument(
                        BertDocumentId.Create(sessionId, userInputId),
                        inputText,
                        Score: null));
                }
            }
            catch (SqliteException ex)
            {
                // FTS5 build option missing or malformed query — treat as unavailable
                // and let the caller fall back to LIKE. Log it loudly so a silent
                // degradation (lexical search quietly switching corpora) doesn't go
                // undetected by ops.
                Serilog.Log.Warning(ex,
                    "FTS5 lexical search failed for query (rendered as: {Match}); falling back to LIKE.",
                    BuildFts5MatchExpression(query));
                return null;
            }
            return results;
        }
    }

    /// <summary>
    /// Wrap user input as an FTS5 phrase to keep punctuation / quotes from blowing up
    /// query parsing. Anything weirder than alphanumerics+spaces becomes a literal
    /// quoted phrase. Doubles inner quotes per FTS5 escaping rules.
    /// </summary>
    private static string BuildFts5MatchExpression(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return "\"\"";
        return "\"" + trimmed.Replace("\"", "\"\"") + "\"";
    }

    public IReadOnlyList<BertStoredDocument> SearchByEmbedding(float[] embedding, int topK)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.Id, d.Text, v.distance
            FROM {BertSearchSchema.VectorTableName} AS v
            JOIN {BertSearchSchema.DocumentTableName} AS d ON v.Id = d.Id
            WHERE v.Embedding MATCH vec_f32(@query)
              AND k = @topK
            ORDER BY v.distance ASC;
            """;
        command.Parameters.AddWithValue("@query", SerializeVector(embedding));
        command.Parameters.AddWithValue("@topK", topK);

        return ReadDocuments(command, includeScore: true);
    }

    public BertStoredDocument? GetSessionCapture(string documentId)
    {
        if (!VectorDatabaseExists) return null;
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        if (!TableExists(connection, BertSearchSchema.SessionDocumentTableName)) return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.SessionDocumentTableName}
            WHERE Id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", documentId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new BertStoredDocument(
            reader.GetString(0),
            reader.GetString(1),
            null);
    }

    public IReadOnlyList<BertStoredDocument> SearchSessionsByText(string query, int topK)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: false);
        if (!TableExists(connection, BertSearchSchema.SessionDocumentTableName)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {BertSearchSchema.SessionDocumentTableName}
            WHERE Text LIKE '%' || @query || '%'
            ORDER BY rowid DESC
            LIMIT @topK;
            """;
        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@topK", topK);

        return ReadDocuments(command, includeScore: false);
    }

    public IReadOnlyList<BertStoredDocument> SearchSessionsByEmbedding(float[] embedding, int topK)
    {
        if (!VectorDatabaseExists) return [];
        using var connection = OpenVectorConnection(loadVectorExtension: true);
        if (!TableExists(connection, BertSearchSchema.SessionVectorTableName)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.Id, d.Text, v.distance
            FROM {BertSearchSchema.SessionVectorTableName} AS v
            JOIN {BertSearchSchema.SessionDocumentTableName} AS d ON v.Id = d.Id
            WHERE v.Embedding MATCH vec_f32(@query)
              AND k = @topK
            ORDER BY v.distance ASC;
            """;
        command.Parameters.AddWithValue("@query", SerializeVector(embedding));
        command.Parameters.AddWithValue("@topK", topK);

        return ReadDocuments(command, includeScore: true);
    }

    public IReadOnlyDictionary<string, BertInputMetadata> GetMetadataByDocumentIds(IReadOnlyCollection<string> documentIds)
    {
        if (documentIds.Count == 0 || !File.Exists(StateDatabasePath))
            return new Dictionary<string, BertInputMetadata>(StringComparer.Ordinal);

        var parsedIds = documentIds
            .Select(id => new { DocumentId = id, Parsed = BertDocumentId.Parse(id) })
            .Where(item => item.Parsed.UserInputId.HasValue)
            .ToList();

        if (parsedIds.Count == 0)
            return new Dictionary<string, BertInputMetadata>(StringComparer.Ordinal);

        var userInputIds = parsedIds
            .Select(item => item.Parsed.UserInputId!.Value)
            .Distinct()
            .ToList();

        using var connection = OpenStateConnection();

        var userInputRows = LoadUserInputRows(connection, userInputIds);
        var fileChangeCounts = LoadFileChangeCounts(connection, userInputIds);

        var result = new Dictionary<string, BertInputMetadata>(StringComparer.Ordinal);
        foreach (var parsed in parsedIds)
        {
            var userInputId = parsed.Parsed.UserInputId!.Value;
            if (!userInputRows.TryGetValue(userInputId, out var row))
                continue;

            result[parsed.DocumentId] = new BertInputMetadata(
                parsed.DocumentId,
                row.SessionId,
                row.UserInputId,
                row.Sequence,
                row.InputText,
                row.GitCommitHash,
                row.TimestampUtc,
                row.Cli,
                row.EnvironmentName,
                row.WorkingDirectory,
                fileChangeCounts.GetValueOrDefault(userInputId));
        }

        return result;
    }

    public IReadOnlyList<BertFileChangeResponse> GetFileChanges(long userInputId)
    {
        if (!File.Exists(StateDatabasePath))
            return [];

        using var connection = OpenStateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FilePath, ChangeType, LinesAdded, LinesDeleted
            FROM InputFileChanges
            WHERE UserInputId = @userInputId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("@userInputId", userInputId);

        var result = new List<BertFileChangeResponse>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BertFileChangeResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return result;
    }

    public IReadOnlyList<BertFileChangeResponse> GetSessionFileChanges(string sessionId)
    {
        if (!File.Exists(StateDatabasePath))
            return [];

        using var connection = OpenStateConnection();
        using var command = connection.CreateCommand();
        // Roll up all per-input file changes for the session: latest ChangeType wins,
        // line counts summed. Matches the "what files changed across the session" view.
        command.CommandText = """
            SELECT ifc.FilePath,
                   (SELECT ifc2.ChangeType
                      FROM InputFileChanges ifc2
                      JOIN UserInputs ui2 ON ui2.Id = ifc2.UserInputId
                      WHERE ifc2.FilePath = ifc.FilePath AND ui2.SessionId = @sessionId
                      ORDER BY ui2.Sequence DESC, ifc2.Id DESC
                      LIMIT 1) AS LastChangeType,
                   COALESCE(SUM(ifc.LinesAdded), 0)   AS TotalAdded,
                   COALESCE(SUM(ifc.LinesDeleted), 0) AS TotalDeleted
            FROM InputFileChanges ifc
            JOIN UserInputs ui ON ui.Id = ifc.UserInputId
            WHERE ui.SessionId = @sessionId
            GROUP BY ifc.FilePath
            ORDER BY (COALESCE(SUM(ifc.LinesAdded), 0) + COALESCE(SUM(ifc.LinesDeleted), 0)) DESC,
                     ifc.FilePath ASC;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        var result = new List<BertFileChangeResponse>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BertFileChangeResponse(
                reader.GetString(0),
                reader.IsDBNull(1) ? "modified" : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return result;
    }

    public IReadOnlyDictionary<string, BertSessionMetadata> GetSessionMetadataByDocumentIds(IReadOnlyCollection<string> documentIds)
    {
        if (documentIds.Count == 0)
            return new Dictionary<string, BertSessionMetadata>(StringComparer.Ordinal);

        var parsed = documentIds
            .Select(id => new { DocumentId = id, Parsed = BertSessionDocumentId.Parse(id) })
            .Where(item => item.Parsed is not null)
            .ToList();
        if (parsed.Count == 0)
            return new Dictionary<string, BertSessionMetadata>(StringComparer.Ordinal);

        var sessionIds = parsed
            .Select(item => item.Parsed!.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Pull per-session totals up front so each chunk-id maps cheaply.
        var sessionRows = new Dictionary<string, SessionSummaryRow>(StringComparer.Ordinal);
        var totalChunksBySession = new Dictionary<string, int>(StringComparer.Ordinal);
        var fileChangesBySession = new Dictionary<string, IReadOnlyList<BertFileChangeResponse>>(StringComparer.Ordinal);

        if (File.Exists(StateDatabasePath))
        {
            using var stateConn = OpenStateConnection();
            using var sessionsCmd = stateConn.CreateCommand();
            sessionsCmd.CommandText = $"""
                SELECT s.Id, s.Cli, s.EnvironmentName, s.WorkingDirectory, s.StartedUTC, s.EndedUTC,
                       (SELECT COUNT(*) FROM UserInputs ui WHERE ui.SessionId = s.Id) AS InputCount
                FROM Sessions s
                WHERE s.Id IN ({BuildInClauseStrings(sessionsCmd, sessionIds, "@sid")});
                """;
            using var reader = sessionsCmd.ExecuteReader();
            while (reader.Read())
            {
                sessionRows[reader.GetString(0)] = new SessionSummaryRow(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(6));
            }

            BatchLoadSessionFileChanges(stateConn, sessionIds, fileChangesBySession);
        }

        if (VectorDatabaseExists)
        {
            using var vecConn = OpenVectorConnection(loadVectorExtension: false);
            if (TableExists(vecConn, BertSearchSchema.SessionDocumentTableName))
            {
                using var chunkCountCmd = vecConn.CreateCommand();
                chunkCountCmd.CommandText = $"""
                    SELECT SessionId, COUNT(*)
                    FROM {BertSearchSchema.SessionDocumentTableName}
                    WHERE SessionId IN ({BuildInClauseStrings(chunkCountCmd, sessionIds, "@csid")})
                    GROUP BY SessionId;
                    """;
                using var reader = chunkCountCmd.ExecuteReader();
                while (reader.Read())
                {
                    totalChunksBySession[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
        }

        var result = new Dictionary<string, BertSessionMetadata>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            var sessionId = p.Parsed!.SessionId;
            sessionRows.TryGetValue(sessionId, out var row);
            result[p.DocumentId] = new BertSessionMetadata(
                p.DocumentId,
                sessionId,
                p.Parsed.ChunkIndex,
                row?.Cli,
                row?.EnvironmentName,
                row?.WorkingDirectory,
                row?.StartedUtc,
                row?.EndedUtc,
                row?.InputCount ?? 0,
                totalChunksBySession.GetValueOrDefault(sessionId),
                fileChangesBySession.GetValueOrDefault(sessionId, Array.Empty<BertFileChangeResponse>()));
        }
        return result;
    }

    /// <summary>
    /// Rolls up file changes for every session in <paramref name="sessionIds"/> in
    /// one query. Replaces an N+1 loop where each <c>GetSessionFileChanges</c> call
    /// opened a fresh state.db connection and ran its own correlated subquery —
    /// for a topK=10 search with unique sessions that was 10 connections + 10
    /// expensive joins per request.
    /// </summary>
    private void BatchLoadSessionFileChanges(
        SqliteConnection connection,
        IReadOnlyList<string> sessionIds,
        IDictionary<string, IReadOnlyList<BertFileChangeResponse>> result)
    {
        if (sessionIds.Count == 0) return;

        // Pre-seed every session with an empty list so callers can rely on
        // GetValueOrDefault returning a real list (avoids "did the query return
        // nothing or did the session have no files" ambiguity).
        var buffers = new Dictionary<string, List<BertFileChangeResponse>>(StringComparer.Ordinal);
        foreach (var sid in sessionIds)
            buffers[sid] = new List<BertFileChangeResponse>();

        using var command = connection.CreateCommand();
        // Correlated subquery joins ifc2 → ui2 to ifc.FilePath AND ui.SessionId so
        // the "latest ChangeType per file" calculation works across sessions in the
        // same scan. The outer GROUP BY adds SessionId so per-session lists are kept
        // distinct.
        command.CommandText = $"""
            SELECT ui.SessionId, ifc.FilePath,
                   (SELECT ifc2.ChangeType
                      FROM InputFileChanges ifc2
                      JOIN UserInputs ui2 ON ui2.Id = ifc2.UserInputId
                      WHERE ifc2.FilePath = ifc.FilePath AND ui2.SessionId = ui.SessionId
                      ORDER BY ui2.Sequence DESC, ifc2.Id DESC
                      LIMIT 1) AS LastChangeType,
                   COALESCE(SUM(ifc.LinesAdded), 0)   AS TotalAdded,
                   COALESCE(SUM(ifc.LinesDeleted), 0) AS TotalDeleted
            FROM InputFileChanges ifc
            JOIN UserInputs ui ON ui.Id = ifc.UserInputId
            WHERE ui.SessionId IN ({BuildInClauseStrings(command, sessionIds, "@fcsid")})
            GROUP BY ui.SessionId, ifc.FilePath
            ORDER BY ui.SessionId,
                     (COALESCE(SUM(ifc.LinesAdded), 0) + COALESCE(SUM(ifc.LinesDeleted), 0)) DESC,
                     ifc.FilePath ASC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            if (!buffers.TryGetValue(sessionId, out var list))
                continue;
            list.Add(new BertFileChangeResponse(
                reader.GetString(1),
                reader.IsDBNull(2) ? "modified" : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        foreach (var (sid, list) in buffers)
            result[sid] = list;
    }

    private SqliteConnection OpenVectorConnection(bool loadVectorExtension)
    {
        EnsureVectorDatabaseExists();

        var connection = new SqliteConnection($"Data Source={_vectorDatabasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        ConfigureBusyTimeout(connection);

        if (loadVectorExtension)
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(SqliteVec0PathResolver.GetPath());
        }

        return connection;
    }

    private SqliteConnection OpenStateConnection()
    {
        var connection = new SqliteConnection($"Data Source={StateDatabasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        ConfigureBusyTimeout(connection);
        return connection;
    }

    private void EnsureVectorDatabaseExists()
    {
        if (!File.Exists(_vectorDatabasePath))
            throw new InvalidOperationException($"BERT vector database not found at '{_vectorDatabasePath}'.");
    }

    private static void ConfigureBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<BertStoredDocument> ReadDocuments(SqliteCommand command, bool includeScore)
    {
        var results = new List<BertStoredDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            double? score = includeScore ? 1.0 - reader.GetDouble(2) : null;
            results.Add(new BertStoredDocument(
                reader.GetString(0),
                reader.GetString(1),
                score));
        }

        return results;
    }

    private static Dictionary<long, UserInputRow> LoadUserInputRows(SqliteConnection connection, IReadOnlyList<long> userInputIds)
    {
        var result = new Dictionary<long, UserInputRow>();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ui.Id, ui.SessionId, ui.Sequence, ui.InputText, ui.GitCommitHash, ui.TimestampUTC,
                   s.Cli, s.EnvironmentName, s.WorkingDirectory
            FROM UserInputs ui
            LEFT JOIN Sessions s ON s.Id = ui.SessionId
            WHERE ui.Id IN ({BuildInClause(command, userInputIds, "@id")});
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = new UserInputRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTime.Parse(reader.GetString(5), null, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));
        }

        return result;
    }

    private static Dictionary<long, int> LoadFileChangeCounts(SqliteConnection connection, IReadOnlyList<long> userInputIds)
    {
        var result = new Dictionary<long, int>();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT UserInputId, COUNT(*)
            FROM InputFileChanges
            WHERE UserInputId IN ({BuildInClause(command, userInputIds, "@changeId")})
            GROUP BY UserInputId;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static string BuildInClause(SqliteCommand command, IReadOnlyList<long> values, string parameterPrefix)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            var name = $"{parameterPrefix}{i}";
            names.Add(name);
            command.Parameters.AddWithValue(name, values[i]);
        }

        return string.Join(", ", names);
    }

    private static string BuildInClauseStrings(SqliteCommand command, IReadOnlyList<string> values, string parameterPrefix)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            var name = $"{parameterPrefix}{i}";
            names.Add(name);
            command.Parameters.AddWithValue(name, values[i]);
        }

        return string.Join(", ", names);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed record UserInputRow(
        long UserInputId,
        string SessionId,
        int Sequence,
        string InputText,
        string? GitCommitHash,
        DateTime TimestampUtc,
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory);

    private sealed record SessionSummaryRow(
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory,
        DateTime? StartedUtc,
        DateTime? EndedUtc,
        int InputCount);
}
