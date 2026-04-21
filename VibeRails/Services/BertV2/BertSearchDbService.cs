using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Utils;

namespace VibeRails.Services.BertV2;

public sealed class BertSearchDbService : IBertSearchDbService
{
    private readonly string _vectorDatabasePath;
    private readonly string _fallbackStateDatabasePath;

    public BertSearchDbService(IBertSettings settings)
    {
        _vectorDatabasePath = Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName);
        _fallbackStateDatabasePath = Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.STATE_FILENAME);
    }

    public string VectorDatabasePath => _vectorDatabasePath;

    public string StateDatabasePath
    {
        get
        {
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
}
