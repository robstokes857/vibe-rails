using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string SearchSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSearchEmbeddings (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            PassageId TEXT NOT NULL, Version TEXT NOT NULL, Embedding TEXT NOT NULL,
            PRIMARY KEY(CardId, PassageId));
        """;

    public async Task<IReadOnlyList<BoardSearchDocument>> GetSearchDocumentsAsync(int offset, CancellationToken cancellationToken = default,
        bool includeContent = true, string? afterCardId = null, int pageSize = 100, bool fileCandidates = true)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // A deferred read snapshot keeps card text, discussion tombstones and cache versions
        // consistent without holding a writer lock while the model runs.
        await using var transaction = connection.BeginTransaction(deferred: true);
        var documents = new List<BoardSearchDocument>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT c.Id, {CardKeySql}, COALESCE(c.DisplayId, {CardKeySql}), c.Title,
                    b.Id, b.Name, col.Id, col.Name, c.ProjectPath, c.Number,
                    COALESCE(pk.Prefix, $legacyPrefix), c.UpdatedUTC, c.Type, c.Priority, c.Assignee,
                    c.Blocked, c.Flagged, CASE WHEN $content THEN c.Description ELSE substr(c.Description,1,260) END,
                    c.AgentMade, EXISTS (SELECT 1 FROM BoardJiraLinks j WHERE j.CardId=c.Id),
                    EXISTS (SELECT 1 FROM BoardAdoLinks a WHERE a.CardId=c.Id), c.GhostReason, COALESCE(c.RemoteCreated, 0)
                FROM BoardCards c JOIN BoardColumns col ON col.Id=c.ColumnId
                JOIN Boards b ON b.Id=col.BoardId
                {CardPrefixJoinSql}
                WHERE c.DeletedUTC IS NULL AND ($after IS NULL OR c.Id > $after)
                ORDER BY c.Id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            command.Parameters.AddWithValue("$legacyPrefix", BoardKeys.LegacyPrefix);
            command.Parameters.AddWithValue("$content", includeContent);
            command.Parameters.AddWithValue("$after", (object?)afterCardId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(pageSize, 1, 100));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var passages = new List<BoardSearchPassage>();
                var title = reader.GetString(3);
                var description = reader.GetString(17);
                documents.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt32(9), reader.GetString(10), ParseDb(reader.GetString(11)), reader.GetString(12), reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetBoolean(15), reader.GetBoolean(16), passages)
                { AgentMade = reader.GetBoolean(18), HasJiraLink = reader.GetBoolean(19), HasAdoLink = reader.GetBoolean(20),
                    GhostReason = reader.IsDBNull(21) ? null : reader.GetString(21),
                    RemoteCreated = reader.GetBoolean(22),
                    KeywordSources = new List<string> { title, description },
                    Sources = new List<SearchSource> { new("title", title), new("description", description, title) } });
            }
        }
        if (documents.Count == 0 || !includeContent) return documents;
        var byId = documents.ToDictionary(document => document.Id, StringComparer.Ordinal);
        var ids = string.Join(',', documents.Select((_, index) => "$id" + index));
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT CardId, 'discussion:' || Id, Body FROM BoardComments m
                WHERE CardId IN ({ids}) AND Kind IN ('comment','note') AND DiscussionHidden=0
                    AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId=m.Id)
                UNION ALL SELECT CardId, 'handoff:' || Id, Json FROM BoardHandoffs WHERE CardId IN ({ids})
                ORDER BY 1, 2
                """;
            for (var index = 0; index < documents.Count; index++) command.Parameters.AddWithValue("$id" + index, documents[index].Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = byId[reader.GetString(0)];
                var text = reader.GetString(2);
                ((List<string>)document.KeywordSources).Add(text);
                ((List<SearchSource>)document.Sources).Add(new(reader.GetString(1), text, document.Title));
            }
        }
        var candidateKeys = await ReadCandidateKeysAsync(connection, transaction, documents, cancellationToken);
        var prefixes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            if (!prefixes.TryGetValue(document.ProjectPath, out var keys))
                prefixes[document.ProjectPath] = keys = await GetRecallKeyPrefixesAsync(document.ProjectPath, cancellationToken);
            var candidateKey = candidateKeys.GetValueOrDefault(document.Id);
            documents[i] = document with
            {
                Sessions = await ReadSessionsAsync(connection, document.Id, cancellationToken),
                Commits = await ReadCommitsAsync(connection, document.Id, cancellationToken, transaction, limit: 3),
                // Candidates come only from snapshots; a card without one has none to read.
                FileCandidates = fileCandidates && candidateKey is not null
                    ? await GetHandoffCandidatesAsync(document.ProjectPath, document.Id, cancellationToken) : [],
                CandidateKey = candidateKey,
                PreviousWork = await ReadHandoffAsync(connection, document.Id, cancellationToken),
                KeyPrefixes = keys.ToArray()
            };
        }
        return documents;
    }

    /// <summary>
    /// Part of every <see cref="BoardSearchDocument.CandidateKey"/>. Change it whenever the candidate
    /// query in <see cref="GetHandoffCandidatesAsync"/> changes, so the search sweep reads them again.
    /// </summary>
    private const string CandidateKeyFormat = "candidates/1";

    /// <summary>
    /// The candidate key of each card on a search page that has a linked commit with a snapshot (VIBE-327).
    /// It reads commit links and the snapshot primary key only: no snapshot text, which the candidate
    /// query parses in full and which made every search sweep read hundreds of megabytes.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadCandidateKeysAsync(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<BoardSearchDocument> documents, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT c.CardId, c.Sha, c.LinkedUTC FROM BoardCommits c
            WHERE c.CardId IN ({string.Join(',', documents.Select((_, index) => "$id" + index))})
                AND EXISTS (SELECT 1 FROM BoardCommitSnapshots s WHERE s.CardId=c.CardId AND s.Sha=c.Sha)
            ORDER BY c.CardId, c.LinkedUTC DESC, c.Sha
            """;
        for (var index = 0; index < documents.Count; index++) command.Parameters.AddWithValue("$id" + index, documents[index].Id);
        var commits = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!commits.TryGetValue(reader.GetString(0), out var text))
                    commits[reader.GetString(0)] = text = new StringBuilder(CandidateKeyFormat);
                text.Append('\n').Append(reader.GetString(1)).Append('@').Append(reader.IsDBNull(2) ? "" : reader.GetString(2));
            }
        return commits.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value.ToString())), 0, 8).ToLowerInvariant(), StringComparer.Ordinal);
    }

    public async Task SaveSearchEmbeddingAsync(string projectPath, string cardId, string passageId, string version,
        float[] embedding, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO BoardSearchEmbeddings(CardId, PassageId, Version, Embedding)
            SELECT Id,$passage,$version,$embedding FROM BoardCards
                WHERE Id=$card AND ProjectPath=$project{ProjectPathCollation} AND DeletedUTC IS NULL
            ON CONFLICT(CardId, PassageId) DO UPDATE SET Version=excluded.Version, Embedding=excluded.Embedding
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$passage", passageId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(embedding, StorageJsonSerializerContext.Default.SingleArray));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
