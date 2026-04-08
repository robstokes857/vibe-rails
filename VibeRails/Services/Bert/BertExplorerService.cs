using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Utils;

namespace VibeRails.Services.Bert;

/// <summary>
/// Read/query surface for captured BERT input documents stored locally on disk.
/// </summary>
public sealed class BertExplorerService : IBertExplorerService, IDisposable
{
    private const string DocumentTableName = "bert_input_documents";
    private const string VectorTableName = "vec_bert_input_documents";
    private const int DefaultTake = 50;
    private const int MaxTake = 200;
    private const int DefaultTopK = 10;
    private const int MaxTopK = 50;
    private const int PreviewLength = 220;

    private readonly ILogger<BertExplorerService> _logger;
    private readonly bool _captureEnabled;
    private readonly string _modelDirectory;
    private readonly string _dataDirectory;
    private readonly string _databasePath;
    private readonly string _fallbackStateDatabasePath;
    private readonly object _embedderLock = new();

    private bool _embedderInitAttempted;
    private BertV2BgeEmbedder? _embedder;
    private string? _embedderInitError;

    public BertExplorerService(IConfiguration configuration, ILogger<BertExplorerService> logger)
    {
        _logger = logger;

        var section = configuration.GetSection("VibeRails:BertCapture");
        _captureEnabled = section.GetValue<bool?>("Enabled") ?? true;

        var installRoot = PathConstants.GetInstallDirPath();
        var configuredModelDirectory = section["ModelDirectory"];
        _modelDirectory = string.IsNullOrWhiteSpace(configuredModelDirectory)
            ? Path.Combine(installRoot, PathConstants.MODELS_SUBDIR, "bertv2")
            : configuredModelDirectory;

        var configuredDataDirectory = section["DataDirectory"];
        _dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(installRoot, PathConstants.VECTOR_SUBDIR, "bert")
            : configuredDataDirectory;

        _databasePath = Path.Combine(_dataDirectory, "bert_user_text_vectors.db");
        _fallbackStateDatabasePath = Path.Combine(installRoot, PathConstants.STATE_FILENAME);
    }

    public BertStatusResponse GetStatus()
    {
        var stateDatabasePath = GetStateDatabasePath();
        var databaseExists = File.Exists(_databasePath);
        var stateDatabaseExists = File.Exists(stateDatabasePath);
        var modelAvailable = HasModelFiles();

        var documentCount = 0;
        var sessionCount = 0;
        DateTime? latestCaptureUtc = null;

        if (databaseExists)
        {
            using var connection = OpenBertConnection(loadVectorExtension: false);
            documentCount = CountDocuments(connection);
            sessionCount = CountSessions(connection);

            var latestDocumentId = GetLatestDocumentId(connection);
            if (!string.IsNullOrWhiteSpace(latestDocumentId))
            {
                var metadata = LoadMetadataByDocumentIds(new[] { latestDocumentId }).Values.FirstOrDefault();
                latestCaptureUtc = metadata?.TimestampUtc;
            }
        }

        return new BertStatusResponse(
            CaptureEnabled: _captureEnabled,
            DatabaseExists: databaseExists,
            StateDatabaseExists: stateDatabaseExists,
            ModelAvailable: modelAvailable,
            SemanticSearchAvailable: databaseExists && modelAvailable,
            DataDirectory: _dataDirectory,
            DatabasePath: _databasePath,
            StateDatabasePath: stateDatabasePath,
            ModelDirectory: _modelDirectory,
            DocumentCount: documentCount,
            SessionCount: sessionCount,
            LatestCaptureUTC: latestCaptureUtc);
    }

    public BertCaptureListResponse GetCaptures(int skip = 0, int take = DefaultTake)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take <= 0 ? DefaultTake : take, 1, MaxTake);

        if (!File.Exists(_databasePath))
            return new BertCaptureListResponse(new List<BertCaptureSummaryResponse>(), 0, skip, take);

        using var connection = OpenBertConnection(loadVectorExtension: false);
        var totalCount = CountDocuments(connection);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {DocumentTableName}
            ORDER BY rowid DESC
            LIMIT @take OFFSET @skip;
            """;
        command.Parameters.AddWithValue("@take", take);
        command.Parameters.AddWithValue("@skip", skip);

        var documents = new List<StoredDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            documents.Add(new StoredDocument(
                reader.GetString(0),
                reader.GetString(1),
                null));
        }

        return new BertCaptureListResponse(
            BuildCaptureSummaries(documents),
            totalCount,
            skip,
            take);
    }

    public BertCaptureDetailResponse? GetCapture(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !File.Exists(_databasePath))
            return null;

        using var connection = OpenBertConnection(loadVectorExtension: false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {DocumentTableName}
            WHERE Id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", documentId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var stored = new StoredDocument(reader.GetString(0), reader.GetString(1), null);
        var metadata = LoadMetadataByDocumentIds(new[] { stored.DocumentId }).Values.FirstOrDefault();
        var fallback = ParseDocumentId(stored.DocumentId);

        var fileChanges = metadata?.UserInputId is long userInputId
            ? LoadFileChanges(userInputId)
            : new List<BertFileChangeResponse>();

        return new BertCaptureDetailResponse(
            DocumentId: stored.DocumentId,
            SessionId: metadata?.SessionId ?? fallback.SessionId ?? string.Empty,
            UserInputId: metadata?.UserInputId ?? fallback.UserInputId,
            Sequence: metadata?.Sequence,
            TimestampUTC: metadata?.TimestampUtc,
            Cli: metadata?.Cli,
            EnvironmentName: metadata?.EnvironmentName,
            WorkingDirectory: metadata?.WorkingDirectory,
            GitCommitHash: metadata?.GitCommitHash ?? ExtractGitCommit(stored.RawText),
            UserText: metadata?.InputText ?? ExtractUserText(stored.RawText),
            FileChanges: fileChanges,
            RawText: NormalizeNewlines(stored.RawText));
    }

    public BertSearchResponse Search(string query, string mode, int topK)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Provide a non-empty query.", nameof(query));

        var normalizedMode = NormalizeMode(mode);
        var normalizedTopK = Math.Clamp(topK <= 0 ? DefaultTopK : topK, 1, MaxTopK);

        if (!File.Exists(_databasePath))
        {
            return new BertSearchResponse(
                Query: query,
                Mode: normalizedMode,
                TopK: normalizedTopK,
                DocumentCount: 0,
                SearchTimeMs: 0,
                Results: new List<BertSearchHitResponse>());
        }

        List<StoredDocument> documents;
        int documentCount;

        var stopwatch = Stopwatch.StartNew();
        if (normalizedMode == "semantic")
        {
            var embedding = GenerateQueryEmbedding(query);
            using var connection = OpenBertConnection(loadVectorExtension: true);
            documentCount = CountDocuments(connection);
            documents = SemanticSearch(connection, embedding, normalizedTopK);
        }
        else
        {
            using var connection = OpenBertConnection(loadVectorExtension: false);
            documentCount = CountDocuments(connection);
            documents = TextSearch(connection, query, normalizedTopK);
        }
        stopwatch.Stop();

        return new BertSearchResponse(
            Query: query,
            Mode: normalizedMode,
            TopK: normalizedTopK,
            DocumentCount: documentCount,
            SearchTimeMs: stopwatch.ElapsedMilliseconds,
            Results: BuildSearchHits(documents));
    }

    private List<BertCaptureSummaryResponse> BuildCaptureSummaries(IReadOnlyList<StoredDocument> documents)
    {
        if (documents.Count == 0)
            return new List<BertCaptureSummaryResponse>();

        var metadataByDocumentId = LoadMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToList());

        return documents.Select(doc =>
        {
            var metadata = metadataByDocumentId.GetValueOrDefault(doc.DocumentId);
            var fallback = ParseDocumentId(doc.DocumentId);

            return new BertCaptureSummaryResponse(
                DocumentId: doc.DocumentId,
                SessionId: metadata?.SessionId ?? fallback.SessionId ?? string.Empty,
                UserInputId: metadata?.UserInputId ?? fallback.UserInputId,
                Sequence: metadata?.Sequence,
                TimestampUTC: metadata?.TimestampUtc,
                Cli: metadata?.Cli,
                EnvironmentName: metadata?.EnvironmentName,
                WorkingDirectory: metadata?.WorkingDirectory,
                GitCommitHash: metadata?.GitCommitHash ?? ExtractGitCommit(doc.RawText),
                UserTextPreview: BuildPreview(metadata?.InputText, doc.RawText),
                FileChangeCount: metadata?.FileChangeCount ?? 0);
        }).ToList();
    }

    private List<BertSearchHitResponse> BuildSearchHits(IReadOnlyList<StoredDocument> documents)
    {
        if (documents.Count == 0)
            return new List<BertSearchHitResponse>();

        var metadataByDocumentId = LoadMetadataByDocumentIds(documents.Select(static doc => doc.DocumentId).ToList());

        return documents.Select(doc =>
        {
            var metadata = metadataByDocumentId.GetValueOrDefault(doc.DocumentId);
            var fallback = ParseDocumentId(doc.DocumentId);

            return new BertSearchHitResponse(
                DocumentId: doc.DocumentId,
                SessionId: metadata?.SessionId ?? fallback.SessionId ?? string.Empty,
                UserInputId: metadata?.UserInputId ?? fallback.UserInputId,
                Sequence: metadata?.Sequence,
                TimestampUTC: metadata?.TimestampUtc,
                Cli: metadata?.Cli,
                EnvironmentName: metadata?.EnvironmentName,
                WorkingDirectory: metadata?.WorkingDirectory,
                GitCommitHash: metadata?.GitCommitHash ?? ExtractGitCommit(doc.RawText),
                UserTextPreview: BuildPreview(metadata?.InputText, doc.RawText),
                FileChangeCount: metadata?.FileChangeCount ?? 0,
                Score: doc.Score);
        }).ToList();
    }

    private Dictionary<string, InputMetadata> LoadMetadataByDocumentIds(IReadOnlyList<string> documentIds)
    {
        if (documentIds.Count == 0)
            return new Dictionary<string, InputMetadata>(StringComparer.Ordinal);

        var stateDatabasePath = GetStateDatabasePath();
        if (!File.Exists(stateDatabasePath))
            return new Dictionary<string, InputMetadata>(StringComparer.Ordinal);

        var parsedIds = documentIds
            .Select(id => new { DocumentId = id, Parsed = ParseDocumentId(id) })
            .Where(item => item.Parsed.UserInputId.HasValue)
            .ToList();

        if (parsedIds.Count == 0)
            return new Dictionary<string, InputMetadata>(StringComparer.Ordinal);

        var ids = parsedIds
            .Select(item => item.Parsed.UserInputId!.Value)
            .Distinct()
            .ToList();

        using var connection = OpenStateConnection(stateDatabasePath);

        var userInputRows = new Dictionary<long, UserInputRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT ui.Id, ui.SessionId, ui.Sequence, ui.InputText, ui.GitCommitHash, ui.TimestampUTC,
                       s.Cli, s.EnvironmentName, s.WorkingDirectory
                FROM UserInputs ui
                LEFT JOIN Sessions s ON s.Id = ui.SessionId
                WHERE ui.Id IN ({BuildInClause(command, ids, "@id")});
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                userInputRows[reader.GetInt64(0)] = new UserInputRow(
                    UserInputId: reader.GetInt64(0),
                    SessionId: reader.GetString(1),
                    Sequence: reader.GetInt32(2),
                    InputText: reader.GetString(3),
                    GitCommitHash: reader.IsDBNull(4) ? null : reader.GetString(4),
                    TimestampUtc: DateTime.Parse(reader.GetString(5), null, DateTimeStyles.RoundtripKind),
                    Cli: reader.IsDBNull(6) ? null : reader.GetString(6),
                    EnvironmentName: reader.IsDBNull(7) ? null : reader.GetString(7),
                    WorkingDirectory: reader.IsDBNull(8) ? null : reader.GetString(8));
            }
        }

        var fileChangeCounts = new Dictionary<long, int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT UserInputId, COUNT(*)
                FROM InputFileChanges
                WHERE UserInputId IN ({BuildInClause(command, ids, "@changeId")})
                GROUP BY UserInputId;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                fileChangeCounts[reader.GetInt64(0)] = reader.GetInt32(1);
            }
        }

        var result = new Dictionary<string, InputMetadata>(StringComparer.Ordinal);
        foreach (var parsed in parsedIds)
        {
            var userInputId = parsed.Parsed.UserInputId!.Value;
            if (!userInputRows.TryGetValue(userInputId, out var row))
                continue;

            result[parsed.DocumentId] = new InputMetadata(
                DocumentId: parsed.DocumentId,
                SessionId: row.SessionId,
                UserInputId: row.UserInputId,
                Sequence: row.Sequence,
                InputText: row.InputText,
                GitCommitHash: row.GitCommitHash,
                TimestampUtc: row.TimestampUtc,
                Cli: row.Cli,
                EnvironmentName: row.EnvironmentName,
                WorkingDirectory: row.WorkingDirectory,
                FileChangeCount: fileChangeCounts.GetValueOrDefault(userInputId));
        }

        return result;
    }

    private List<BertFileChangeResponse> LoadFileChanges(long userInputId)
    {
        var stateDatabasePath = GetStateDatabasePath();
        if (!File.Exists(stateDatabasePath))
            return new List<BertFileChangeResponse>();

        using var connection = OpenStateConnection(stateDatabasePath);
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
                FilePath: reader.GetString(0),
                ChangeType: reader.GetString(1),
                LinesAdded: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                LinesDeleted: reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return result;
    }

    private List<StoredDocument> TextSearch(SqliteConnection connection, string query, int topK)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Text
            FROM {DocumentTableName}
            WHERE Text LIKE '%' || @query || '%'
            ORDER BY rowid DESC
            LIMIT @topK;
            """;
        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@topK", topK);

        var results = new List<StoredDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new StoredDocument(
                reader.GetString(0),
                reader.GetString(1),
                null));
        }

        return results;
    }

    private List<StoredDocument> SemanticSearch(SqliteConnection connection, float[] embedding, int topK)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.Id, d.Text, v.distance
            FROM {VectorTableName} AS v
            JOIN {DocumentTableName} AS d ON v.Id = d.Id
            WHERE v.Embedding MATCH vec_f32(@query)
              AND k = @topK
            ORDER BY v.distance ASC;
            """;
        command.Parameters.AddWithValue("@query", SerializeVector(embedding));
        command.Parameters.AddWithValue("@topK", topK);

        var results = new List<StoredDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var similarity = 1.0 - reader.GetDouble(2);
            results.Add(new StoredDocument(
                reader.GetString(0),
                reader.GetString(1),
                similarity));
        }

        return results;
    }

    private float[] GenerateQueryEmbedding(string query)
    {
        if (!_captureEnabled)
            throw new InvalidOperationException("BERT capture is disabled in appsettings.json.");

        lock (_embedderLock)
        {
            if (_embedder is null)
            {
                if (_embedderInitAttempted)
                    throw new InvalidOperationException(_embedderInitError ?? "BERT embedder is not available.");

                _embedderInitAttempted = true;

                var modelPath = Path.Combine(_modelDirectory, "model.onnx");
                var vocabPath = Path.Combine(_modelDirectory, "vocab.txt");
                if (!File.Exists(modelPath) || !File.Exists(vocabPath))
                {
                    _embedderInitError = $"Model files not found. Expected {modelPath} and {vocabPath}.";
                    throw new InvalidOperationException(_embedderInitError);
                }

                try
                {
                    _embedder = new BertV2BgeEmbedder(modelPath, vocabPath);
                }
                catch (Exception ex)
                {
                    _embedderInitError = $"Failed to initialize the BERT embedder: {ex.Message}";
                    _logger.LogWarning(ex, "[BERT] Explorer failed to initialize query embedder.");
                    throw new InvalidOperationException(_embedderInitError, ex);
                }
            }

            return _embedder.GenerateEmbedding(query);
        }
    }

    private SqliteConnection OpenBertConnection(bool loadVectorExtension)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        ConfigureBusyTimeout(connection);

        if (loadVectorExtension)
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(GetVec0Path());
        }

        return connection;
    }

    private static SqliteConnection OpenStateConnection(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        ConfigureBusyTimeout(connection);
        return connection;
    }

    private static void ConfigureBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    private static int CountDocuments(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {DocumentTableName};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountSessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(DISTINCT substr(Id, 1, instr(Id, ':') - 1))
            FROM {DocumentTableName}
            WHERE instr(Id, ':') > 0;
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? GetLatestDocumentId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id
            FROM {DocumentTableName}
            ORDER BY rowid DESC
            LIMIT 1;
            """;
        return command.ExecuteScalar() as string;
    }

    private static string BuildPreview(string? inputText, string rawText)
    {
        var text = !string.IsNullOrWhiteSpace(inputText)
            ? NormalizeWhitespace(inputText)
            : NormalizeWhitespace(ExtractUserText(rawText));

        if (string.IsNullOrWhiteSpace(text))
            text = NormalizeWhitespace(rawText);

        if (text.Length <= PreviewLength)
            return text;

        return text[..PreviewLength] + "...";
    }

    private static string NormalizeMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "" or null or "semantic" => "semantic",
            "text" or "keyword" => "text",
            _ => throw new ArgumentException("Mode must be 'semantic' or 'text'.", nameof(mode))
        };
    }

    private static ParsedDocumentId ParseDocumentId(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return new ParsedDocumentId(null, null);

        var separatorIndex = documentId.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= documentId.Length - 1)
            return new ParsedDocumentId(documentId, null);

        var sessionId = documentId[..separatorIndex];
        if (long.TryParse(documentId[(separatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userInputId))
            return new ParsedDocumentId(sessionId, userInputId);

        return new ParsedDocumentId(sessionId, null);
    }

    private static string ExtractUserText(string rawText)
    {
        var normalized = NormalizeNewlines(rawText);
        const string startMarker = "user_text:\n";
        const string endMarker = "\nfile_changes:";

        var startIndex = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
            return string.Empty;

        startIndex += startMarker.Length;
        var endIndex = normalized.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = normalized.Length;

        return normalized[startIndex..endIndex].Trim();
    }

    private static string? ExtractGitCommit(string rawText)
    {
        var normalized = NormalizeNewlines(rawText);
        const string marker = "git_commit:";
        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        markerIndex += marker.Length;
        var lineEnd = normalized.IndexOf('\n', markerIndex);
        if (lineEnd < 0)
            lineEnd = normalized.Length;

        var value = normalized[markerIndex..lineEnd].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', NormalizeNewlines(value)
            .Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
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

    private bool HasModelFiles()
    {
        return File.Exists(Path.Combine(_modelDirectory, "model.onnx"))
            && File.Exists(Path.Combine(_modelDirectory, "vocab.txt"));
    }

    private string GetStateDatabasePath()
    {
        var configured = ParserConfigs.GetStatePath();
        return string.IsNullOrWhiteSpace(configured) ? _fallbackStateDatabasePath : configured;
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string GetVec0Path()
    {
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? "linux-arm64"
                    : "linux-x64";

        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ".dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ".dylib"
                : ".so";

        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "vec0");
        if (File.Exists(candidate + ext))
            return candidate;

        return "vec0";
    }

    public void Dispose()
    {
        lock (_embedderLock)
        {
            _embedder?.Dispose();
            _embedder = null;
        }
    }

    private sealed record StoredDocument(string DocumentId, string RawText, double? Score);

    private sealed record ParsedDocumentId(string? SessionId, long? UserInputId);

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

    private sealed record InputMetadata(
        string DocumentId,
        string SessionId,
        long UserInputId,
        int Sequence,
        string InputText,
        string? GitCommitHash,
        DateTime TimestampUtc,
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory,
        int FileChangeCount);
}
