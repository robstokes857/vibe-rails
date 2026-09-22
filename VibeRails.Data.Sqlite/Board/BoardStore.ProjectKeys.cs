using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// Card key prefixes: in <c>VR-32</c>, <c>VR</c> is the project's prefix and 32 its card number.
/// A project gets its prefix once, from its folder name, inside the transaction that numbers its
/// first card, and the prefix never changes afterwards, so no key a person has already read,
/// committed or linked is rewritten. Projects that already numbered cards when board/11 arrived keep
/// <see cref="BoardKeys.LegacyPrefix"/>. So does a project whose first card an older binary numbers
/// beside this build: that binary shows every card as VB-n, and the two must keep agreeing.
/// </summary>
public sealed partial class BoardStore
{
    // Properties, not constants: they interpolate ProjectPathCollation (see CardSequenceReseedSql).
    internal static string ProjectKeysSchemaSql => $"""
        CREATE TABLE IF NOT EXISTS BoardProjectKeys (
            ProjectPath TEXT PRIMARY KEY{ProjectPathCollation},
            Prefix TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardProjectKeys_Prefix ON BoardProjectKeys(Prefix);
        """;

    /// <summary>The prefix a card row displays, for queries that include <see cref="CardPrefixJoinSql"/>.</summary>
    private const string CardPrefixSql = "COALESCE(pk.Prefix, '" + BoardKeys.LegacyPrefix + "')";

    /// <summary>Joins a card row <c>c</c> to its project's prefix row <c>pk</c>, absent for projects an older binary numbered.</summary>
    private static string CardPrefixJoinSql => $"LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath = c.ProjectPath{ProjectPathCollation}";

    /// <summary>
    /// board/11: the prefix table, seeded with VB for every project that already numbers cards so
    /// that upgrading changes no existing key. Pure SQL; the fallback in <see cref="CardPrefixSql"/>
    /// is what an older binary's later projects display until this build numbers a card there.
    /// </summary>
    private static void ApplyProjectKeysMigration(SqliteConnection db, SqliteTransaction transaction)
    {
        SqliteSchema.Execute(db, transaction, ProjectKeysSchemaSql);
        using var seed = db.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = """
            INSERT OR IGNORE INTO BoardProjectKeys (ProjectPath, Prefix, CreatedUTC)
                SELECT ProjectPath, $prefix, $now FROM BoardCardSequences
                UNION SELECT ProjectPath, $prefix, $now FROM BoardCards;
            """;
        seed.Parameters.AddWithValue("$prefix", BoardKeys.LegacyPrefix);
        seed.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        seed.ExecuteNonQuery();
    }

    /// <summary>
    /// The prefix this project's keys display, assigning one when the project has none. Runs in the
    /// caller's write transaction, before the card number is allocated, so the two never race.
    /// A project that already numbers cards without a prefix row was numbered by a build that
    /// predates prefixes, or by an older binary running beside this one; its keys have been shown
    /// as VB-n and stay that way.
    /// </summary>
    private static async Task<string> EnsureProjectKeyPrefixAsync(SqliteConnection connection, SqliteTransaction transaction, string project, CancellationToken cancellationToken)
    {
        var existing = await ScalarStringAsync(connection, transaction,
            $"SELECT Prefix FROM BoardProjectKeys WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        if (existing is not null)
            return existing;

        var numbered = await ScalarLongAsync(connection, transaction, $"""
            SELECT EXISTS (SELECT 1 FROM BoardCardSequences WHERE ProjectPath = $project{ProjectPathCollation})
                OR EXISTS (SELECT 1 FROM BoardCards WHERE ProjectPath = $project{ProjectPathCollation});
            """, ("$project", project), cancellationToken) != 0;
        var prefix = numbered
            ? BoardKeys.LegacyPrefix
            : await ChooseUnusedPrefixAsync(connection, transaction, project, cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO BoardProjectKeys (ProjectPath, Prefix, CreatedUTC) VALUES ($project, $prefix, $now);";
        insert.Parameters.AddWithValue("$project", project);
        insert.Parameters.AddWithValue("$prefix", prefix);
        insert.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return prefix;
    }

    /// <summary>
    /// The folder name's initials, then its first letters, then random letters: the first one no
    /// other project on this machine already displays. Every project in board.db shares the check,
    /// which is the whole point of the prefix — VB-1 must not mean three different cards.
    /// </summary>
    private static async Task<string> ChooseUnusedPrefixAsync(SqliteConnection connection, SqliteTransaction transaction, string project, CancellationToken cancellationToken)
    {
        foreach (var candidate in BoardKeys.DerivePrefixCandidates(project))
        {
            if (!await IsPrefixUsedAsync(connection, transaction, candidate, cancellationToken))
                return candidate;
        }
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var random = BoardKeys.RandomPrefix();
            if (!await IsPrefixUsedAsync(connection, transaction, random, cancellationToken))
                return random;
        }
        throw new BoardConflictException("Could not pick an unused card key prefix for this project.");
    }

    private static async Task<bool> IsPrefixUsedAsync(SqliteConnection connection, SqliteTransaction transaction, string prefix, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM BoardProjectKeys WHERE Prefix = $prefix);",
            ("$prefix", prefix), cancellationToken) != 0;
}
