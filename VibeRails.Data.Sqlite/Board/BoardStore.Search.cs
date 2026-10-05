using System.Text.Json;
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
        bool includeContent = true, string? afterCardId = null, int pageSize = 100)
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
                    c.Blocked, c.Flagged, CASE WHEN $content THEN c.Description ELSE substr(c.Description,1,260) END
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
                { KeywordSources = new List<string> { title, description },
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
        var prefixes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            if (!prefixes.TryGetValue(document.ProjectPath, out var keys))
                prefixes[document.ProjectPath] = keys = await GetRecallKeyPrefixesAsync(document.ProjectPath, cancellationToken);
            documents[i] = document with
            {
                Sessions = await ReadSessionsAsync(connection, document.Id, cancellationToken),
                Commits = await ReadCommitsAsync(connection, document.Id, cancellationToken, transaction, limit: 3),
                FileCandidates = await GetHandoffCandidatesAsync(document.ProjectPath, document.Id, cancellationToken),
                PreviousWork = await ReadHandoffAsync(connection, document.Id, cancellationToken),
                KeyPrefixes = keys.ToArray()
            };
        }
        return documents;
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
