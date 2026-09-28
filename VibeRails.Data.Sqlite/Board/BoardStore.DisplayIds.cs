using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string CardDisplayIdSql = "COALESCE(c.DisplayId, " + CardKeySql + ")";

    private static void ApplyDisplayIdsMigration(SqliteConnection db, SqliteTransaction transaction)
    {
        SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE Boards ADD COLUMN DisplayPrefix TEXT;");
        SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN DisplayId TEXT;");
        SqliteSchema.Execute(db, transaction, $"""
            UPDATE BoardCards SET DisplayId = COALESCE(CardKey,
                COALESCE((SELECT Prefix FROM BoardProjectKeys pk WHERE pk.ProjectPath = BoardCards.ProjectPath{ProjectPathCollation}), 'VB') || '-' || Number) WHERE DisplayId IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BoardCards_DisplayId ON BoardCards(ProjectPath, DisplayId COLLATE NOCASE) WHERE DisplayId IS NOT NULL;
            CREATE TABLE IF NOT EXISTS BoardDisplaySequences (
                ProjectPath TEXT NOT NULL{ProjectPathCollation},
                Prefix TEXT NOT NULL COLLATE NOCASE,
                LastNumber INTEGER NOT NULL,
                PRIMARY KEY (ProjectPath, Prefix)
            );
            """);
    }

    private static async Task<string?> DisplayIdOwnerAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string displayId, string cardId, CancellationToken ct) =>
        await ScalarStringAsync(db, transaction, $"""
            SELECT c.Id FROM BoardCards c {CardPrefixJoinSql}
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id <> $id
                AND ({CardDisplayIdSql} = $display COLLATE NOCASE OR {CardKeySql} = $display COLLATE NOCASE)
            LIMIT 1;
            """, ("$project", project), ct, ("$display", displayId), ("$id", cardId));

    private static async Task<string> AllocateDisplayIdAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string boardId, string cardId, CancellationToken ct)
    {
        var board = await ReadBoardAsync(db, transaction, project, boardId, ct)
            ?? throw new BoardValidationException("Board not found.");
        var prefix = board.EffectiveDisplayPrefix;
        while (true)
        {
            var number = checked((int)await ScalarLongAsync(db, transaction, """
                INSERT INTO BoardDisplaySequences (ProjectPath, Prefix, LastNumber) VALUES ($project, $prefix, 1)
                ON CONFLICT(ProjectPath, Prefix) DO UPDATE SET LastNumber = MIN(LastNumber + 1, 2147483647)
                RETURNING LastNumber;
                """, ("$project", project), ct, ("$prefix", prefix)));
            // An edited/imported maximum label must not prevent future card creation.
            if (number == int.MaxValue) number = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var candidate = BoardKeys.Format(prefix, number);
            if (await DisplayIdOwnerAsync(db, transaction, project, candidate, cardId, ct) is null) return candidate;
        }
    }

    private static async Task SeedDisplaySequenceAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string displayId, CancellationToken ct)
    {
        if (!BoardKeys.TryParse(displayId, out var prefix, out var number)) return;
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardDisplaySequences (ProjectPath, Prefix, LastNumber) VALUES ($project, $prefix, $number)
            ON CONFLICT(ProjectPath, Prefix) DO UPDATE SET LastNumber = MAX(LastNumber, $number);
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$number", number);
        await command.ExecuteNonQueryAsync(ct);
    }

    // A server-assigned display label wins over a local label. Renaming the local occupant and
    // recording its correction share the incoming write transaction. Immutable keys never move.
    private static async Task<string> ResolveDisplayIdAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string boardId, string cardId, string requested, bool remote, CancellationToken ct)
    {
        var displayId = BoardDisplayIds.Normalize(requested);
        await SeedDisplaySequenceAsync(db, transaction, project, displayId, ct);
        var ownerId = await DisplayIdOwnerAsync(db, transaction, project, displayId, cardId, ct);
        if (ownerId is null) return displayId;
        if (!remote) throw new BoardConflictException($"Display ID {displayId} is already in use.");

        var owner = (await ReadCardAsync(db, transaction, project, ownerId, ct, includeDeleted: true))!;
        // Historical immutable keys are reserved forever. The incoming card receives a new label.
        if (string.Equals(owner.Key, displayId, StringComparison.OrdinalIgnoreCase))
            return await AllocateDisplayIdAsync(db, transaction, project, boardId, cardId, ct);
        var replacement = await AllocateDisplayIdAsync(db, transaction, project, owner.BoardId, owner.Id, ct);
        await using var rename = db.CreateCommand();
        rename.Transaction = transaction;
        rename.CommandText = "UPDATE BoardCards SET DisplayId = $display, UpdatedUTC = $now WHERE Id = $id;";
        rename.Parameters.AddWithValue("$display", replacement);
        rename.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        rename.Parameters.AddWithValue("$id", owner.Id);
        await rename.ExecuteNonQueryAsync(ct);
        await LogCardChangedAsync(db, transaction, owner, owner with { StoredDisplayId = replacement, UpdatedUtc = DateTime.UtcNow },
            null, null, BoardAuthor.System(), ct);
        return displayId;
    }

    private static async Task ReconcileSyncedDisplayIdAsync(SqliteConnection db, SqliteTransaction transaction,
        BoardCardRecord card, string? requested, CancellationToken ct)
    {
        if (requested is null || string.Equals(requested, card.DisplayId, StringComparison.OrdinalIgnoreCase)) return;
        await LogCardChangedAsync(db, transaction, card with { StoredDisplayId = requested }, card,
            null, null, BoardAuthor.System(), ct);
    }
}
