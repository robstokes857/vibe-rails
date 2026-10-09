using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string JiraDeliverySchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardJiraDeliveries (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            ConnectionId TEXT NOT NULL,
            IssueId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            SourceId TEXT NOT NULL,
            Body TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'pending',
            Message TEXT,
            Url TEXT,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            UNIQUE(ConnectionId, IssueId, Kind, SourceId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardJiraDeliveries_Pending ON BoardJiraDeliveries(Status, CreatedUTC);
        CREATE INDEX IF NOT EXISTS IX_BoardJiraDeliveries_Card ON BoardJiraDeliveries(CardId, CreatedUTC);
        CREATE TRIGGER IF NOT EXISTS BoardComments_QueueJira AFTER INSERT ON BoardComments
        WHEN NEW.Kind IN ('comment', 'note') AND NEW.RemoteSeq IS NULL AND NEW.SyncBoardId IS NULL
            AND NOT (NEW.AuthorKind = 'agent' AND NEW.AuthorLabel = 'Jira' AND NEW.AuthorCli IS NULL AND NEW.SessionId IS NULL)
            AND COALESCE(NEW.DiscussionHidden, 0) = 0
            AND CASE WHEN json_valid(NEW.Changes) THEN COALESCE(json_extract(NEW.Changes, '$.jiraCopy'), 0) ELSE 0 END = 0
            AND CASE WHEN json_valid(NEW.Changes) THEN COALESCE(json_extract(NEW.Changes, '$.syncToJira.to'), 1) ELSE 1 END != 0
        BEGIN
            INSERT OR IGNORE INTO BoardJiraDeliveries
                (Id, CardId, ConnectionId, IssueId, Kind, SourceId, Body, CreatedUTC, UpdatedUTC)
            SELECT lower(hex(randomblob(16))), c.Id, j.SiteId, j.IssueId, 'comment', NEW.Id,
                NEW.AuthorLabel || ': ' || NEW.Body, NEW.CreatedUTC, NEW.CreatedUTC
            FROM BoardCards c JOIN BoardJiraLinks j ON j.CardId = c.Id
            JOIN BoardJiraConnections conn ON conn.Id = j.SiteId AND conn.ProjectPath = c.ProjectPath
            WHERE c.Id = NEW.CardId AND c.DeletedUTC IS NULL;
        END;
        """;

    private static async Task QueueJiraSessionAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardSessionRecord session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardJiraDeliveries
                (Id, CardId, ConnectionId, IssueId, Kind, SourceId, Body, CreatedUTC, UpdatedUTC)
            SELECT lower(hex(randomblob(16))), c.Id, j.SiteId, j.IssueId, 'session', $session,
                $body, $now, $now
            FROM BoardCards c JOIN BoardJiraLinks j ON j.CardId = c.Id
            JOIN BoardJiraConnections conn ON conn.Id = j.SiteId AND conn.ProjectPath = c.ProjectPath
            WHERE c.Id = $card AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$card", session.CardId);
        command.Parameters.AddWithValue("$session", session.SessionId);
        command.Parameters.AddWithValue("$body", "VibeRails session linked: " + session.DisplayName);
        command.Parameters.AddWithValue("$now", ToDb(session.CreatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<BoardJiraDelivery>> GetPendingJiraDeliveriesAsync(CancellationToken cancellationToken = default) =>
        ReadJiraDeliveriesAsync(null, null, cancellationToken);

    public Task<IReadOnlyList<BoardJiraDelivery>> GetJiraDeliveriesAsync(string projectPath, string cardId, CancellationToken cancellationToken = default) =>
        ReadJiraDeliveriesAsync(NormalizeProjectPath(projectPath), cardId, cancellationToken);

    private async Task<IReadOnlyList<BoardJiraDelivery>> ReadJiraDeliveriesAsync(string? project, string? cardId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.Id, d.CardId, c.ProjectPath, d.ConnectionId, d.IssueId, d.Kind, d.SourceId,
                d.Body, d.Status, d.Message, d.Url
            FROM BoardJiraDeliveries d JOIN BoardCards c ON c.Id = d.CardId
            WHERE c.DeletedUTC IS NULL
                {(project is null ? "AND d.Status = 'pending'" : $"AND c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $card")}
            ORDER BY d.CreatedUTC {(project is null ? "ASC" : "DESC")}, d.Id LIMIT 100;
            """;
        if (project is not null)
        {
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$card", cardId!);
        }
        var results = new List<BoardJiraDelivery>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return results;
    }

    public async Task<bool> SetJiraDeliveryAsync(string id, string expectedStatus, string status, string? message, string? url, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardJiraDeliveries SET Status = $status, Message = $message, Url = COALESCE($url, Url), UpdatedUTC = $now
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

    public async Task RecoverJiraDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardJiraDeliveries SET Status = 'uncertain',
                Message = 'Delivery was interrupted. Check Jira and Sharing links before posting again.', UpdatedUTC = $now
            WHERE Status = 'sending';
            """;
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
