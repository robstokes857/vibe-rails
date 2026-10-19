using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <summary>
    /// board-ado-delivery/1: the outbound Azure DevOps ledger, the same shape as
    /// <c>BoardJiraDeliveries</c>, and a future-comment trigger that writes only to it. The trigger
    /// honours the generic <c>Changes.syncToJira.to=false</c> flag and <c>jiraCopy</c>, so an older
    /// binary's suppressed or copied comment is never sent either. It leaves out comments by the
    /// Azure DevOps agent (the pull's own notes), hidden discussion and rows pulled from
    /// viberails.ai. <c>INSERT OR IGNORE</c>, so an older binary's comment write never fails here.
    /// </summary>
    private const string AdoDeliverySchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAdoDeliveries (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            ConnectionId TEXT NOT NULL,
            WorkItemId INTEGER NOT NULL,
            Kind TEXT NOT NULL,
            SourceId TEXT NOT NULL,
            Body TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'pending',
            Message TEXT,
            Url TEXT,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            UNIQUE(ConnectionId, WorkItemId, Kind, SourceId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAdoDeliveries_Pending ON BoardAdoDeliveries(Status, CreatedUTC);
        CREATE INDEX IF NOT EXISTS IX_BoardAdoDeliveries_Card ON BoardAdoDeliveries(CardId, CreatedUTC);
        CREATE TRIGGER IF NOT EXISTS BoardComments_QueueAdo AFTER INSERT ON BoardComments
        WHEN NEW.Kind IN ('comment', 'note') AND NEW.RemoteSeq IS NULL AND NEW.SyncBoardId IS NULL
            AND NOT (NEW.AuthorKind = 'agent' AND NEW.AuthorLabel = 'Azure DevOps' AND NEW.AuthorCli IS NULL AND NEW.SessionId IS NULL)
            AND COALESCE(NEW.DiscussionHidden, 0) = 0
            AND CASE WHEN json_valid(NEW.Changes) THEN COALESCE(json_extract(NEW.Changes, '$.jiraCopy'), 0) ELSE 0 END = 0
            AND CASE WHEN json_valid(NEW.Changes) THEN COALESCE(json_extract(NEW.Changes, '$.syncToJira.to'), 1) ELSE 1 END != 0
        BEGIN
            INSERT OR IGNORE INTO BoardAdoDeliveries
                (Id, CardId, ConnectionId, WorkItemId, Kind, SourceId, Body, CreatedUTC, UpdatedUTC)
            SELECT lower(hex(randomblob(16))), c.Id, a.ConnectionId, a.WorkItemId, 'comment', NEW.Id,
                NEW.AuthorLabel || ': ' || NEW.Body, NEW.CreatedUTC, NEW.CreatedUTC
            FROM BoardCards c JOIN BoardAdoLinks a ON a.CardId = c.Id
            JOIN BoardAdoConnections conn ON conn.Id = a.ConnectionId AND conn.ProjectPath = c.ProjectPath
            WHERE c.Id = NEW.CardId AND c.DeletedUTC IS NULL;
        END;
        """;

    private static async Task QueueAdoSessionAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardSessionRecord session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardAdoDeliveries
                (Id, CardId, ConnectionId, WorkItemId, Kind, SourceId, Body, CreatedUTC, UpdatedUTC)
            SELECT lower(hex(randomblob(16))), c.Id, a.ConnectionId, a.WorkItemId, 'session', $session,
                $body, $now, $now
            FROM BoardCards c JOIN BoardAdoLinks a ON a.CardId = c.Id
            JOIN BoardAdoConnections conn ON conn.Id = a.ConnectionId AND conn.ProjectPath = c.ProjectPath
            WHERE c.Id = $card AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$card", session.CardId);
        command.Parameters.AddWithValue("$session", session.SessionId);
        command.Parameters.AddWithValue("$body", "VibeRails session linked: " + session.DisplayName);
        command.Parameters.AddWithValue("$now", ToDb(session.CreatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BoardAdoDelivery>> GetPendingAdoDeliveriesAsync(CancellationToken cancellationToken = default) =>
        ReadAdoDeliveriesAsync(null, null, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<BoardAdoDelivery>> GetAdoDeliveriesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default) =>
        ReadAdoDeliveriesAsync(NormalizeProjectPath(projectPath), cardId, cancellationToken);

    private async Task<IReadOnlyList<BoardAdoDelivery>> ReadAdoDeliveriesAsync(string? project, string? cardId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.Id, d.CardId, c.ProjectPath, d.ConnectionId, d.WorkItemId, d.Kind, d.SourceId,
                d.Body, d.Status, d.Message, d.Url
            FROM BoardAdoDeliveries d JOIN BoardCards c ON c.Id = d.CardId
            WHERE c.DeletedUTC IS NULL
                {(project is null ? "AND d.Status = 'pending'" : $"AND c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $card")}
            ORDER BY d.CreatedUTC {(project is null ? "ASC" : "DESC")}, d.Id LIMIT 100;
            """;
        if (project is not null)
        {
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$card", cardId!);
        }
        var results = new List<BoardAdoDelivery>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return results;
    }

    /// <inheritdoc />
    public async Task<bool> SetAdoDeliveryAsync(string id, string expectedStatus, string status, string? message, string? url, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardAdoDeliveries SET Status = $status, Message = $message, Url = COALESCE($url, Url), UpdatedUTC = $now
            WHERE Id = $id AND Status = $expected;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expected", expectedStatus);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task RecoverAdoDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardAdoDeliveries SET Status = 'uncertain',
                Message = 'Delivery was interrupted. Check Azure DevOps and Sharing links before posting again.', UpdatedUTC = $now
            WHERE Status = 'sending';
            """;
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
