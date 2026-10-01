using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string ReviewsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardReviews (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            CreatedUTC TEXT NOT NULL,
            ReportedUTC TEXT,
            RecordJson TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardReviews_Session ON BoardReviews(json_extract(RecordJson, '$.sessionId'));
        CREATE INDEX IF NOT EXISTS IX_BoardReviews_Card ON BoardReviews(CardId, CreatedUTC DESC);
        """;

    public async Task<bool> SaveReviewAsync(string projectPath, BoardReviewRecord review, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO BoardReviews (Id, CardId, CreatedUTC, ReportedUTC, RecordJson)
            SELECT $id, c.Id, $created, $reported, $json FROM BoardCards c
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
              AND ($requireSession = 0 OR EXISTS (SELECT 1 FROM {AllSessionsSql} s WHERE s.CardId = c.Id AND s.SessionId = $session))
            ON CONFLICT(Id) DO UPDATE SET ReportedUTC = excluded.ReportedUTC, RecordJson = excluded.RecordJson
            WHERE BoardReviews.CardId = excluded.CardId AND BoardReviews.ReportedUTC IS NULL
              AND BoardReviews.CreatedUTC = excluded.CreatedUTC
              AND json_extract(BoardReviews.RecordJson, '$.reviewer') = json_extract(excluded.RecordJson, '$.reviewer')
              AND json_extract(BoardReviews.RecordJson, '$.provider') = json_extract(excluded.RecordJson, '$.provider')
              AND json_extract(BoardReviews.RecordJson, '$.runId') IS json_extract(excluded.RecordJson, '$.runId')
              AND (json_extract(BoardReviews.RecordJson, '$.capturedUtc') IS NULL
                OR json_extract(BoardReviews.RecordJson, '$.capturedUtc') IS json_extract(excluded.RecordJson, '$.capturedUtc'))
              AND (json_extract(BoardReviews.RecordJson, '$.sessionId') IS NULL
                OR json_extract(BoardReviews.RecordJson, '$.sessionId') IS json_extract(excluded.RecordJson, '$.sessionId'));
            """;
        command.Parameters.AddWithValue("$requireSession", review.CapturedUtc is not null || review.ReportedUtc is not null ? 1 : 0);
        command.Parameters.AddWithValue("$session", (object?)review.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", review.Id);
        command.Parameters.AddWithValue("$card", review.CardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$created", ToDb(review.CreatedUtc));
        command.Parameters.AddWithValue("$reported", review.ReportedUtc is {} at ? ToDb(at) : DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(review, StorageJsonSerializerContext.Default.BoardReviewRecord));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        if (review.ReportedUtc is not null)
        {
            // One discussion stream. The comment refers to the canonical report instead of copying it.
            await using var comment = connection.CreateCommand();
            comment.Transaction = transaction;
            comment.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind)
                VALUES ($id, $card, 'agent', $label, $cli, $session, $body, $now, 'comment');
                """;
            comment.Parameters.AddWithValue("$id", "cm_" + Guid.NewGuid().ToString("N"));
            comment.Parameters.AddWithValue("$card", review.CardId);
            comment.Parameters.AddWithValue("$label", review.Reviewer);
            comment.Parameters.AddWithValue("$cli", review.Provider);
            comment.Parameters.AddWithValue("$session", (object?)review.SessionId ?? DBNull.Value);
            comment.Parameters.AddWithValue("$body", $"Code review saved: {review.Result}. See Code reviews, report {review.Id} (get_board_reviews reviewId={review.Id}). Scope: {review.ScopeDescription}");
            comment.Parameters.AddWithValue("$now", ToDb(review.ReportedUtc.Value));
            await comment.ExecuteNonQueryAsync(cancellationToken);
            await TouchCardAsync(connection, transaction, review.CardId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<BoardReviewRecord>> GetReviewsAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default)
        => await ReadReviewsAsync(projectPath, cardId, null, offset, cancellationToken);

    public async Task<BoardReviewRecord?> GetReviewAsync(string projectPath, string cardId, string reviewId, CancellationToken cancellationToken = default)
        => (await ReadReviewsAsync(projectPath, cardId, reviewId, 0, cancellationToken)).FirstOrDefault();

    public async Task<BoardReviewRecord?> GetLatestReviewAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
        => (await ReadReviewsAsync(projectPath, cardId, null, 0, cancellationToken, latest: true)).FirstOrDefault();

    public async Task<BoardReviewRecord?> GetReviewForSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default)
        => (await ReadReviewsAsync(projectPath, cardId, null, 0, cancellationToken, sessionId: sessionId)).FirstOrDefault();

    private async Task<IReadOnlyList<BoardReviewRecord>> ReadReviewsAsync(string projectPath, string cardId, string? id, int offset, CancellationToken ct, bool latest = false, string? sessionId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.RecordJson FROM BoardReviews r JOIN BoardCards c ON c.Id = r.CardId
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
              AND ($id IS NULL OR r.Id = $id)
              AND ($session IS NULL OR json_extract(r.RecordJson, '$.sessionId') = $session)
              AND ($latest = 0 OR r.ReportedUTC IS NOT NULL)
            ORDER BY CASE WHEN $latest = 1 THEN r.ReportedUTC ELSE r.CreatedUTC END DESC, r.Id DESC LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$latest", latest ? 1 : 0);
        command.Parameters.AddWithValue("$limit", latest ? 1 : 50);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<BoardReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardReviewRecord)!);
        return rows;
    }
}
