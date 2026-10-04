using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

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
        bool includeContent = true, string? afterCardId = null)
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
                ORDER BY c.Id LIMIT 100 OFFSET $offset
                """;
            command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            command.Parameters.AddWithValue("$legacyPrefix", BoardKeys.LegacyPrefix);
            command.Parameters.AddWithValue("$content", includeContent);
            command.Parameters.AddWithValue("$after", (object?)afterCardId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var passages = new List<BoardSearchPassage>();
                var title = reader.GetString(3);
                var description = reader.GetString(17);
                if (includeContent)
                {
                    AddSearchPassages(passages, "title", "", title);
                    if (description.Length > 0) AddSearchPassages(passages, "card", title, description);
                }
                else AddSearchPassages(passages, "card", title, description);
                documents.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt32(9), reader.GetString(10), ParseDb(reader.GetString(11)), reader.GetString(12), reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetBoolean(15), reader.GetBoolean(16), passages)
                { KeywordSources = new List<string> { title, description } });
            }
        }
        if (documents.Count == 0 || !includeContent) return documents;
        var byId = documents.ToDictionary(document => document.Id, StringComparer.Ordinal);
        var ids = string.Join(',', documents.Select((_, index) => "$id" + index));
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT CardId, Id, Body FROM BoardComments m
                WHERE CardId IN ({ids}) AND Kind IN ('comment','note') AND DiscussionHidden=0
                    AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId=m.Id)
                UNION ALL SELECT CardId, Id, Json FROM BoardHandoffs WHERE CardId IN ({ids})
                ORDER BY CardId, Id
                """;
            for (var index = 0; index < documents.Count; index++) command.Parameters.AddWithValue("$id" + index, documents[index].Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = byId[reader.GetString(0)];
                var text = reader.GetString(2);
                ((List<string>)document.KeywordSources).Add(text);
                AddSearchPassages((List<BoardSearchPassage>)document.Passages, reader.GetString(1), document.Title, text);
            }
        }
        var vectors = new Dictionary<(string Card, string Passage), (string Version, string Embedding)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT CardId, PassageId, Version, Embedding FROM BoardSearchEmbeddings WHERE CardId IN ({ids})";
            for (var index = 0; index < documents.Count; index++) command.Parameters.AddWithValue("$id" + index, documents[index].Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                vectors[(reader.GetString(0), reader.GetString(1))] = (reader.GetString(2), reader.GetString(3));
        }
        return documents.Select(document => document with
        {
            Passages = document.Passages.Select(passage => vectors.TryGetValue((document.Id, passage.Id), out var cached) && cached.Version == passage.Version
                ? passage with { Embedding = JsonSerializer.Deserialize(cached.Embedding, StorageJsonSerializerContext.Default.SingleArray) }
                : passage).ToArray()
        }).ToArray();
    }

    private static void AddSearchPassages(List<BoardSearchPassage> passages, string source, string title, string text)
    {
        // BERT WordPiece can produce a token per character for punctuation and dense code.
        // A complete input stays under 512 even then (96 title + newline + 384 body +
        // two special tokens). The full title has separate passages; clipping its context
        // prefix never loses title coverage. Overlap and boundaries preserve surrogate pairs.
        const int size = 384;
        const int overlap = 64;
        var prefixLength = SearchBoundary(title, Math.Min(96, title.Length));
        var prefix = prefixLength > 0 ? title[..prefixLength] + "\n" : "";
        for (var offset = 0; ;)
        {
            var end = SearchBoundary(text, Math.Min(offset + size, text.Length));
            var content = prefix + text[offset..end];
            var version = "bge-small-en-v1.5:board-search-2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            passages.Add(new(source + ":" + offset, content, version, null));
            if (end >= text.Length) break;
            offset = SearchBoundary(text, end - overlap);
        }
    }

    private static int SearchBoundary(string text, int index) => index > 0 && index < text.Length
        && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;

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
