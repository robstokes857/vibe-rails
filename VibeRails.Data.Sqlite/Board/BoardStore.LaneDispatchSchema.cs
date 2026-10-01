using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Additive: old queues and writers retain their contracts. Delete triggers write only to
    // this new ledger, including cancellations by older card/settings writers. No backfill.
    private static void ApplyLaneDispatchSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchema.Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS BoardLaneAutomationDispatch (
                EventKey TEXT NOT NULL,
                JobId INTEGER NOT NULL,
                CardId TEXT NOT NULL,
                ColumnId TEXT NOT NULL,
                DueUnixMs INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Reason TEXT NOT NULL,
                RunId TEXT NULL,
                LastAttemptUnixMs INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (EventKey, JobId)
            );
            CREATE INDEX IF NOT EXISTS IX_BoardLaneAutomationDispatch_Card
                ON BoardLaneAutomationDispatch(CardId, DueUnixMs DESC);
            """);
        foreach (var table in new[] { "BoardPendingAutomations", "BoardPendingAdditionalAutomations" })
            SqliteSchema.Execute(connection, transaction, $"""
                CREATE TRIGGER IF NOT EXISTS {table}_RecordCancellation BEFORE DELETE ON {table} BEGIN
                    INSERT INTO BoardLaneAutomationDispatch (EventKey, JobId, CardId, ColumnId, DueUnixMs, Status, Reason)
                    VALUES (OLD.EventKey, OLD.JobId, OLD.CardId, OLD.ColumnId, OLD.DueUnixMs, 'Cancelled',
                        CASE
                            WHEN NOT EXISTS (SELECT 1 FROM BoardCards WHERE Id = OLD.CardId AND DeletedUTC IS NULL)
                                THEN 'Card was deleted.'
                            WHEN NOT EXISTS (SELECT 1 FROM BoardCards WHERE Id = OLD.CardId AND ColumnId = OLD.ColumnId)
                                THEN 'Card left the destination lane; a later entry starts a new settling period.'
                            ELSE 'Lane Automation assignment changed or the pending entry was removed.'
                        END)
                    ON CONFLICT(EventKey, JobId) DO UPDATE SET Status = excluded.Status, Reason = excluded.Reason
                        WHERE BoardLaneAutomationDispatch.Status = 'Waiting';
                END;
                """);
    }
}
