using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string CardDisplayIdSql = "COALESCE(c.DisplayId, " + CardKeySql + ")";

    /// <summary>
    /// The cards a short key <c>PREFIX-n</c> names: a legacy card numbered n (under the project's prefix
    /// or the VB alias an older binary shows) and a stored <c>PREFIX-RRRRR-n</c> key by its own number.
    /// Binds <c>$prefix</c>, <c>$number</c> and <c>$short</c>; needs <see cref="CardPrefixJoinSql"/>.
    /// Card lookup and display-label reservation share it, so a label can never claim a name a key answers to.
    /// </summary>
    private const string ShortKeyMatchSql = $"""
        ((c.CardKey IS NULL AND c.Number = $number AND ($prefix = '{BoardKeys.LegacyPrefix}' OR $prefix = {CardPrefixSql}))
          OR c.CardKey = $short
          OR c.CardKey GLOB ($prefix || '-?????-' || $number)
          OR ($prefix = '{BoardKeys.LegacyPrefix}' AND c.CardKey GLOB ({CardPrefixSql} || '-?????-' || $number)))
        """;

    /// <summary>
    /// A card already answering to a label. <see cref="Reserved"/> is true when it answers through its
    /// immutable key or one of that key's short forms: keys never move, so that card cannot give the label up.
    /// </summary>
    private sealed record DisplayIdOwner(string Id, bool Reserved);

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

    /// <summary>
    /// The other card, deleted or not, that already answers to <paramref name="displayId"/>: by its label,
    /// its immutable key, or a short form of that key. A key claim sorts first.
    /// </summary>
    private static async Task<DisplayIdOwner?> DisplayIdOwnerAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string displayId, string cardId, CancellationToken ct)
    {
        var shortForm = BoardKeys.TryParse(displayId, out var prefix, out var number);
        // IFNULL: the GLOB and equality arms are NULL, not false, for a legacy row without a stored key.
        var keyClaim = $"IFNULL({CardKeySql} = $display COLLATE NOCASE OR ($number > 0 AND {ShortKeyMatchSql}), 0)";
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT c.Id, {keyClaim} AS Reserved FROM BoardCards c {CardPrefixJoinSql}
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id <> $id
                AND ({CardDisplayIdSql} = $display COLLATE NOCASE OR {keyClaim})
            ORDER BY Reserved DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", cardId);
        command.Parameters.AddWithValue("$display", displayId);
        command.Parameters.AddWithValue("$prefix", shortForm ? prefix : "");
        command.Parameters.AddWithValue("$number", shortForm ? number : 0);
        command.Parameters.AddWithValue("$short", shortForm ? BoardKeys.Format(prefix, number) : "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new DisplayIdOwner(reader.GetString(0), reader.GetInt64(1) != 0) : null;
    }

    /// <summary>
    /// True when a label or an imported key already spells a short form of <c>{keyPrefix}-RRRRR-{number}</c>
    /// (<c>{keyPrefix}-{number}</c>, or <c>VB-{number}</c>). Local key minting skips such a number, so a label
    /// never shadows a key minted after it.
    /// </summary>
    private static async Task<bool> ShortKeyClaimedAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string keyPrefix, int number, string cardId, CancellationToken ct) =>
        await DisplayIdOwnerAsync(db, transaction, project, BoardKeys.Format(keyPrefix, number), cardId, ct) is not null
        || (keyPrefix != BoardKeys.LegacyPrefix
            && await DisplayIdOwnerAsync(db, transaction, project, BoardKeys.Format(BoardKeys.LegacyPrefix, number), cardId, ct) is not null);

    /// <summary>
    /// The next free label on the board's display prefix. <paramref name="keyNumber"/> is the number in
    /// the card's own key: when keys also answer to the display prefix, the card's own short key is its
    /// label whenever no other card claims it.
    /// </summary>
    private static async Task<string> AllocateDisplayIdAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string boardId, string cardId, CancellationToken ct, int keyNumber = 0)
    {
        var board = await ReadBoardAsync(db, transaction, project, boardId, ct)
            ?? throw new BoardValidationException("Board not found.");
        var prefix = board.EffectiveDisplayPrefix;
        // A label prefix that card keys also answer to (the project's key prefix, or the VB alias every
        // project resolves) starts at the card-number high-water mark: every such label below it is
        // some card's short key. A new card's number is already allocated, so its label is its own short key.
        var keyPrefix = await IsKeyPrefixAsync(db, transaction, project, prefix, ct);
        if (keyPrefix && keyNumber is > 0 and < int.MaxValue)
        {
            var own = BoardKeys.Format(prefix, keyNumber);
            if (await DisplayIdOwnerAsync(db, transaction, project, own, cardId, ct) is null)
            {
                await SeedDisplaySequenceAsync(db, transaction, project, own, ct);
                return own;
            }
        }
        var floor = keyPrefix
            ? Math.Min(int.MaxValue, await ScalarLongAsync(db, transaction,
                $"SELECT LastNumber FROM BoardCardSequences WHERE ProjectPath = $project{ProjectPathCollation};", ("$project", project), ct))
            : 0;
        while (true)
        {
            var number = checked((int)await ScalarLongAsync(db, transaction, """
                INSERT INTO BoardDisplaySequences (ProjectPath, Prefix, LastNumber) VALUES ($project, $prefix, MAX(1, $floor))
                ON CONFLICT(ProjectPath, Prefix) DO UPDATE SET LastNumber = MAX(MIN(LastNumber + 1, 2147483647), $floor)
                RETURNING LastNumber;
                """, ("$project", project), ct, ("$prefix", prefix), ("$floor", floor)));
            // An edited/imported maximum label must not prevent future card creation.
            if (number == int.MaxValue) number = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var candidate = BoardKeys.Format(prefix, number);
            if (await DisplayIdOwnerAsync(db, transaction, project, candidate, cardId, ct) is null) return candidate;
        }
    }

    /// <summary>True when card keys of this project also answer to <paramref name="prefix"/>-n.</summary>
    private static async Task<bool> IsKeyPrefixAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string prefix, CancellationToken ct) =>
        string.Equals(prefix, BoardKeys.LegacyPrefix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(prefix, await ScalarStringAsync(db, transaction, $"""
            SELECT COALESCE((SELECT Prefix FROM BoardProjectKeys WHERE ProjectPath = $project{ProjectPathCollation}), '{BoardKeys.LegacyPrefix}');
            """, ("$project", project), ct), StringComparison.OrdinalIgnoreCase);

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
    // keyNumber is the number in the labelled card's own key, for a replacement label.
    private static async Task<string> ResolveDisplayIdAsync(SqliteConnection db, SqliteTransaction transaction,
        string project, string boardId, string cardId, string requested, bool remote, int keyNumber, CancellationToken ct)
    {
        var displayId = BoardDisplayIds.Normalize(requested);
        await SeedDisplaySequenceAsync(db, transaction, project, displayId, ct);
        var owner = await DisplayIdOwnerAsync(db, transaction, project, displayId, cardId, ct);
        if (owner is null) return displayId;
        if (!remote)
            throw new BoardConflictException(owner.Reserved
                ? $"Display ID {displayId} is already another card's key. Pick a different display ID."
                : $"Display ID {displayId} is already in use.");

        // A card's immutable key and its short forms are reserved forever. The incoming card receives a new label.
        if (owner.Reserved)
            return await AllocateDisplayIdAsync(db, transaction, project, boardId, cardId, ct, keyNumber);
        var occupant = (await ReadCardAsync(db, transaction, project, owner.Id, ct, includeDeleted: true))!;
        var replacement = await AllocateDisplayIdAsync(db, transaction, project, occupant.BoardId, occupant.Id, ct, ParseKeyNumber(occupant.Key));
        await using var rename = db.CreateCommand();
        rename.Transaction = transaction;
        rename.CommandText = "UPDATE BoardCards SET DisplayId = $display, UpdatedUTC = $now WHERE Id = $id;";
        rename.Parameters.AddWithValue("$display", replacement);
        rename.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        rename.Parameters.AddWithValue("$id", occupant.Id);
        await rename.ExecuteNonQueryAsync(ct);
        await LogCardChangedAsync(db, transaction, occupant, occupant with { StoredDisplayId = replacement, UpdatedUtc = DateTime.UtcNow },
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
