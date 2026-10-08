using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Additive snapshots: old card/queue writers participate without changing their tables.
    // Each entry gets a random identity before any of the existing AFTER triggers run.
    private static void ApplyWorkflowSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchema.Execute(connection, transaction, """
            CREATE TABLE BoardLaneWorkflows (
                Id TEXT PRIMARY KEY, CardId TEXT NOT NULL, ColumnId TEXT NOT NULL,
                CreatedUnixMs INTEGER NOT NULL, Current INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IX_BoardLaneWorkflows_Current ON BoardLaneWorkflows(CardId) WHERE Current = 1;
            CREATE INDEX IX_BoardLaneWorkflows_Column ON BoardLaneWorkflows(ColumnId, Current);
            CREATE TABLE BoardLaneWorkflowSteps (
                WorkflowId TEXT NOT NULL REFERENCES BoardLaneWorkflows(Id), JobId INTEGER NOT NULL,
                Position INTEGER NOT NULL, EventKey TEXT NULL,
                PRIMARY KEY (WorkflowId, JobId)
            );
            CREATE UNIQUE INDEX IX_BoardLaneWorkflowSteps_Event ON BoardLaneWorkflowSteps(EventKey, JobId);
            CREATE TABLE BoardLaneStepReports (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, EventKey TEXT NOT NULL, JobId INTEGER NOT NULL,
                Status TEXT NOT NULL, Summary TEXT NOT NULL, RunId TEXT NULL, ReviewId TEXT NULL,
                SessionId TEXT NULL, CreatedUTC TEXT NOT NULL
            );
            CREATE INDEX IX_BoardLaneStepReports_Event ON BoardLaneStepReports(EventKey, JobId, Id DESC);
            """);
        foreach (var (suffix, operation, condition) in new[] {
            ("Insert", "INSERT", ""), ("Move", "UPDATE OF ColumnId", "WHEN OLD.ColumnId <> NEW.ColumnId") })
            SqliteSchema.Execute(connection, transaction, $"""
                CREATE TRIGGER BoardCards_Workflow_{suffix} BEFORE {operation} ON BoardCards {condition} BEGIN
                    UPDATE BoardLaneWorkflows SET Current = 0 WHERE CardId = NEW.Id AND Current = 1;
                    INSERT INTO BoardLaneWorkflows (Id, CardId, ColumnId, CreatedUnixMs)
                    SELECT lower(hex(randomblob(16))), NEW.Id, NEW.ColumnId, CAST(unixepoch('subsec') * 1000 AS INTEGER)
                    WHERE EXISTS (SELECT 1 FROM BoardLaneAutomations WHERE ColumnId = NEW.ColumnId AND JobId IS NOT NULL);
                    INSERT INTO BoardLaneWorkflowSteps (WorkflowId, JobId, Position)
                    SELECT w.Id, a.JobId, 0 FROM BoardLaneWorkflows w JOIN BoardLaneAutomations a ON a.ColumnId = w.ColumnId
                    WHERE w.CardId = NEW.Id AND w.Current = 1 AND a.JobId IS NOT NULL
                    UNION ALL
                    SELECT w.Id, a.JobId, a.Position FROM BoardLaneWorkflows w JOIN BoardLaneAdditionalAutomations a ON a.ColumnId = w.ColumnId
                    WHERE w.CardId = NEW.Id AND w.Current = 1;
                END;
                """);
        foreach (var table in new[] { "BoardPendingAutomations", "BoardPendingAdditionalAutomations" })
            SqliteSchema.Execute(connection, transaction, $"""
                CREATE TRIGGER {table}_Workflow AFTER INSERT ON {table} BEGIN
                    UPDATE BoardLaneWorkflowSteps SET EventKey = NEW.EventKey
                    WHERE JobId = NEW.JobId AND WorkflowId IN (
                        SELECT Id FROM BoardLaneWorkflows WHERE CardId = NEW.CardId AND ColumnId = NEW.ColumnId AND Current = 1);
                END;
                """);
        foreach (var operation in new[] { "UPDATE", "DELETE" })
            SqliteSchema.Execute(connection, transaction, $"""
                CREATE TRIGGER BoardLaneAutomations_Workflow_{operation} AFTER {operation} ON BoardLaneAutomations BEGIN
                    UPDATE BoardLaneWorkflows SET Current = 0 WHERE ColumnId = OLD.ColumnId AND Current = 1;
                END;
                """);
    }
}
