using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlySet<string>> GetRecallKeyPrefixesAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Prefix FROM BoardProjectKeys WHERE ProjectPath=$project{ProjectPathCollation}
            UNION SELECT COALESCE(DisplayPrefix, $default) FROM Boards WHERE ProjectPath=$project{ProjectPathCollation}
            UNION SELECT Prefix FROM BoardDisplaySequences WHERE ProjectPath=$project{ProjectPathCollation}
            UNION SELECT substr(CardKey, 1, instr(CardKey, '-') - 1) FROM BoardCards
                WHERE ProjectPath=$project{ProjectPathCollation} AND CardKey IS NOT NULL
            UNION SELECT substr(DisplayId, 1, instr(DisplayId, '-') - 1) FROM BoardCards
                WHERE ProjectPath=$project{ProjectPathCollation} AND DisplayId IS NOT NULL
            UNION SELECT o.KeyPrefix FROM BoardSharedOrigins o JOIN Boards b ON b.Id=o.BoardId
                WHERE b.ProjectPath=$project{ProjectPathCollation};
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$default", BoardDisplayIds.DefaultPrefix(project));
        // FindCardAsync accepts VB-n as the compatibility alias even on newly prefixed projects.
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BoardKeys.LegacyPrefix };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0) && reader.GetString(0).Length > 0) prefixes.Add(reader.GetString(0));
        return prefixes;
    }

    private const string RecallSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardHandoffs (
            Id TEXT PRIMARY KEY, CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Json TEXT NOT NULL, CreatedUTC TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_BoardHandoffs_Card ON BoardHandoffs(CardId, CreatedUTC DESC, Id DESC);
        CREATE TABLE IF NOT EXISTS BoardRecallEmbeddings (
            CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Version TEXT NOT NULL, Embedding TEXT NOT NULL);
        """;

    public async Task<BoardHandoff?> SaveHandoffAsync(string projectPath, string cardId, BoardHandoff handoff, BoardAuthor author, CancellationToken cancellationToken = default)
    {
        author = author with { Purpose = author.Kind == BoardAuthor.AgentKind && author.SessionId is { } session
            ? await FindSessionPurposeAsync(projectPath, cardId, session, cancellationToken) : null };
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return null;
        var saved = handoff with { Id = "handoff_" + Guid.NewGuid().ToString("N"), Author = author, CreatedUtc = DateTime.UtcNow };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO BoardHandoffs(Id, CardId, Json, CreatedUTC) VALUES($id,$card,$json,$time)";
        command.Parameters.AddWithValue("$id", saved.Id);
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved, StorageJsonSerializerContext.Default.BoardHandoff));
        command.Parameters.AddWithValue("$time", ToDb(saved.CreatedUtc.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
        // A handoff is part of the discussion; its structured entry points have separate storage.
        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = """
            INSERT INTO BoardComments(Id,CardId,AuthorKind,AuthorLabel,AuthorCli,SessionId,Body,CreatedUTC,Kind,Changes)
            VALUES($id,$card,$kind,$label,$cli,$session,$body,$time,'comment',$purpose)
            """;
        receipt.Parameters.AddWithValue("$id", "cm_" + Guid.NewGuid().ToString("N"));
        receipt.Parameters.AddWithValue("$card", card.Id);
        receipt.Parameters.AddWithValue("$kind", author.Kind);
        receipt.Parameters.AddWithValue("$label", author.Label);
        receipt.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$purpose", (object?)BoardCommentPurpose.Changes(author) ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$time", ToDb(saved.CreatedUtc.Value));
        receipt.Parameters.AddWithValue("$body", $"Previous work ({saved.Id})\nOutcome: {saved.Outcome}\nDecisions: {saved.Decisions}\nValidation: {saved.Validation}\nOutstanding: {saved.Outstanding}\nStart here:\n" +
            string.Join("\n", saved.Files.Select(f => $"@{f.Path} [{f.Role}] {f.Symbol} — {f.Reason}; commit {f.Commit ?? "unspecified"}")));
        await receipt.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<BoardHandoff?> GetHandoffAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        return card is null ? null : await ReadHandoffAsync(connection, card.Id, cancellationToken);
    }

    private static async Task<BoardHandoff?> ReadHandoffAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM BoardHandoffs WHERE CardId=$card ORDER BY CreatedUTC DESC, Id DESC LIMIT 1";
        command.Parameters.AddWithValue("$card", cardId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.BoardHandoff);
    }

    public async Task<IReadOnlyList<BoardFileReference>> GetHandoffCandidatesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_extract(f.value, '$.fileName'), s.Sha FROM BoardCommitSnapshots s
            JOIN BoardCommits c ON c.CardId=s.CardId AND c.Sha=s.Sha,
            json_each(s.SnapshotJson, '$.files') f WHERE s.CardId=$card
            ORDER BY c.LinkedUTC DESC, s.Sha, f.key LIMIT 20
            """;
        command.Parameters.AddWithValue("$card", card.Id);
        var files = new List<BoardFileReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0)) files.Add(new(reader.GetString(0), "Changed in linked commit; curate before using as an entry point", "candidate", Commit: reader.GetString(1)));
        return files;
    }

    public async Task<IReadOnlyList<BoardRecallDocument>> GetRecallDocumentsAsync(string projectPath, int offset, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.Id, {CardKeySql}, COALESCE(c.DisplayId, {CardKeySql}), c.Title,
                c.Description, c.UpdatedUTC, h.Json, h.Id, e.Version, e.Embedding
            FROM BoardCards c LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath=c.ProjectPath
            LEFT JOIN BoardHandoffs h ON h.Id=(SELECT Id FROM BoardHandoffs WHERE CardId=c.Id ORDER BY CreatedUTC DESC, Id DESC LIMIT 1)
            LEFT JOIN BoardRecallEmbeddings e ON e.CardId=c.Id
            WHERE c.ProjectPath=$project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ORDER BY c.Id LIMIT 100 OFFSET $offset
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var result = new List<BoardRecallDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = "bge-small-en-v1.5:1:" + reader.GetString(5) + ":" + (reader.IsDBNull(7) ? "" : reader.GetString(7));
            var handoff = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize(reader.GetString(6), StorageJsonSerializerContext.Default.BoardHandoff);
            var text = reader.GetString(3) + "\n" +
                (handoff is null ? "" : $"{handoff.Outcome}\n{handoff.Decisions}\n{handoff.Outstanding}\n") + reader.GetString(4);
            var vector = !reader.IsDBNull(8) && reader.GetString(8) == version
                ? JsonSerializer.Deserialize(reader.GetString(9), StorageJsonSerializerContext.Default.SingleArray) : null;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), text, version, vector));
        }
        return result;
    }

    public async Task SaveRecallEmbeddingAsync(string projectPath, string cardId, string version, float[] embedding, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO BoardRecallEmbeddings(CardId, Version, Embedding)
            SELECT Id,$version,$embedding FROM BoardCards WHERE Id=$card AND ProjectPath=$project{ProjectPathCollation} AND DeletedUTC IS NULL
            ON CONFLICT(CardId) DO UPDATE SET Version=excluded.Version, Embedding=excluded.Embedding
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(embedding, StorageJsonSerializerContext.Default.SingleArray));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
