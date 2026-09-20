namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Expand board/6 without changing its tables or triggers: old schedulers can still consume
    // the first job, while new schedulers consume the additional jobs independently. Existing
    // single selections, revisions and pending events require no backfill or destructive upgrade.
    private const string AdditionalLaneAutomationSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardLaneAdditionalAutomations (
            ColumnId TEXT NOT NULL REFERENCES BoardLaneAutomations(ColumnId) ON DELETE CASCADE,
            JobId INTEGER NOT NULL,
            Position INTEGER NOT NULL,
            PRIMARY KEY (ColumnId, JobId)
        );
        CREATE TABLE IF NOT EXISTS BoardPendingAdditionalAutomations (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            ColumnId TEXT NOT NULL,
            JobId INTEGER NOT NULL,
            EventKey TEXT NOT NULL,
            DueUnixMs INTEGER NOT NULL,
            PRIMARY KEY (CardId, JobId),
            FOREIGN KEY (ColumnId, JobId) REFERENCES BoardLaneAdditionalAutomations(ColumnId, JobId) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_BoardPendingAdditionalAutomations_Due ON BoardPendingAdditionalAutomations(DueUnixMs);
        CREATE TRIGGER IF NOT EXISTS BoardLaneAutomations_ClearAdditional AFTER UPDATE ON BoardLaneAutomations BEGIN
            DELETE FROM BoardLaneAdditionalAutomations WHERE ColumnId = NEW.ColumnId;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardCards_AdditionalLaneAutomation_Insert AFTER INSERT ON BoardCards BEGIN
            INSERT INTO BoardPendingAdditionalAutomations (CardId, ColumnId, JobId, EventKey, DueUnixMs)
            SELECT NEW.Id, NEW.ColumnId, a.JobId, lower(hex(randomblob(16))), CAST(unixepoch('subsec') * 1000 AS INTEGER) + 60000
            FROM BoardLaneAdditionalAutomations a WHERE a.ColumnId = NEW.ColumnId;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardCards_AdditionalLaneAutomation_Move AFTER UPDATE OF ColumnId ON BoardCards
        WHEN OLD.ColumnId <> NEW.ColumnId BEGIN
            DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = NEW.Id;
            INSERT INTO BoardPendingAdditionalAutomations (CardId, ColumnId, JobId, EventKey, DueUnixMs)
            SELECT NEW.Id, NEW.ColumnId, a.JobId, lower(hex(randomblob(16))), CAST(unixepoch('subsec') * 1000 AS INTEGER) + 60000
            FROM BoardLaneAdditionalAutomations a WHERE a.ColumnId = NEW.ColumnId;
        END;
        """;
}
