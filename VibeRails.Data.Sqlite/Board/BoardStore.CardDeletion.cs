using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Identity/delivery receipts only: no retained card text, discussion, files or recordings.
    // No FK to the card/board: identities must remain reserved after either is removed.
    private const string CardDeletionSchemaSql = """
        CREATE TABLE BoardDeletedCards (
            CardId TEXT PRIMARY KEY,
            CardKey TEXT NOT NULL COLLATE NOCASE UNIQUE,
            ProjectPath TEXT NOT NULL,
            Number INTEGER NOT NULL,
            BoardId TEXT NOT NULL,
            DeletedUTC TEXT NOT NULL,
            EntryId TEXT NOT NULL UNIQUE,
            AuthorKind TEXT NOT NULL,
            AuthorLabel TEXT NOT NULL,
            AuthorCli TEXT,
            RemoteSeq INTEGER,
            Managed INTEGER NOT NULL
        );
        CREATE INDEX IX_BoardDeletedCards_Outbox ON BoardDeletedCards(BoardId, RemoteSeq);
        CREATE TABLE BoardDeletedTrackerItems (
            Provider TEXT NOT NULL,
            ConnectionId TEXT NOT NULL,
            ItemId TEXT NOT NULL,
            CardId TEXT NOT NULL,
            ProjectPath TEXT NOT NULL,
            DeletedUTC TEXT NOT NULL,
            PRIMARY KEY (Provider, ConnectionId, ItemId)
        );
        """;

    /// <summary>
    /// Permanently removes one live, project-scoped card and its owned data atomically.
    /// Session recordings, shared state, historical soft deletions and backups are independent.
    /// </summary>
    public async Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default, BoardAuthor? author = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return false;
        author ??= BoardAuthor.User();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardDeletedCards
                (CardId, CardKey, ProjectPath, Number, BoardId, DeletedUTC, EntryId, AuthorKind, AuthorLabel, AuthorCli, RemoteSeq, Managed)
            VALUES ($id, $key, $project, $number, $board, $now, $entry, $kind, $label, $cli,
                CASE WHEN EXISTS (SELECT 1 FROM BoardSyncLinks WHERE BoardId = $board)
                  AND EXISTS (SELECT 1 FROM BoardSyncLog WHERE CardId = $id AND Kind = 'created'
                    AND (SyncBoardId IS NULL OR SyncBoardId = $board) AND (RemoteSeq IS NULL OR RemoteSeq > 0))
                THEN NULL ELSE 0 END, $managed);
            INSERT OR IGNORE INTO BoardDeletedTrackerItems
                SELECT 'jira', SiteId, IssueId, CardId, $project, $now FROM BoardJiraLinks WHERE CardId = $id;
            INSERT OR IGNORE INTO BoardDeletedTrackerItems
                SELECT 'ado', ConnectionId, CAST(WorkItemId AS TEXT), CardId, $project, $now FROM BoardAdoLinks WHERE CardId = $id;
            DELETE FROM BoardLaneStepReports WHERE EventKey IN (
                SELECT s.EventKey FROM BoardLaneWorkflowSteps s JOIN BoardLaneWorkflows w ON w.Id = s.WorkflowId WHERE w.CardId = $id
                UNION SELECT EventKey FROM BoardLaneAutomationDispatch WHERE CardId = $id
                UNION SELECT EventKey FROM BoardPendingAutomations WHERE CardId = $id
                UNION SELECT EventKey FROM BoardPendingAdditionalAutomations WHERE CardId = $id);
            DELETE FROM BoardLaneWorkflowSteps WHERE WorkflowId IN (SELECT Id FROM BoardLaneWorkflows WHERE CardId = $id);
            DELETE FROM BoardLaneWorkflows WHERE CardId = $id;
            DELETE FROM BoardCodeQualityExemptions WHERE CardId = $id;
            DELETE FROM BoardSyncSkippedEntries WHERE BoardId = $board AND CardKey = $key;
            DELETE FROM BoardCards WHERE Id = $id;
            DELETE FROM BoardDesktopActivity WHERE CardId = $id;
            DELETE FROM BoardLaneAutomationDispatch WHERE CardId = $id;
            """;
        command.Parameters.AddWithValue("$id", card.Id);
        command.Parameters.AddWithValue("$key", card.Key);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$number", card.Number);
        command.Parameters.AddWithValue("$managed", card.WorkflowMode == BoardWorkflowModes.Agent && string.IsNullOrWhiteSpace(card.ManagerSelection) ? 1 : 0);
        command.Parameters.AddWithValue("$board", card.BoardId);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$entry", NewId("log"));
        command.Parameters.AddWithValue("$kind", author.Kind);
        command.Parameters.AddWithValue("$label", author.Label);
        command.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RenumberColumnAsync(connection, transaction, card.ColumnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
