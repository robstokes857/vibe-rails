using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

/// <summary>
/// Schema-only remnant of the retired per-tool_result capture table. The writer
/// (<c>CompressionCaptureStore</c>) was removed on 2026-09-17: its re-sight UPDATE could not use the
/// partial ContentHash index and scanned the whole table under state.db's single writer lock,
/// hundreds of times per request, starving every other writer in every vb process. The always-on
/// exchange log in proxy_exchanges.db holds the same bytes.
///
/// The table and its migration receipt stay exactly as they were so existing databases need no
/// migration and a fresh database matches docs/schema/state.sql. Dropping the table is a breaking
/// migration for a later cleanup. Rows already captured are the user's data and are left alone.
/// </summary>
internal static class CompressionCapturesSchema
{
    internal static void EnsureSchema(SqliteConnection connection)
    {
        SqliteMigrationRunner.RequireGenerationAtMost(connection, StateDatabaseSchema.Generation, "state.db");
        SqliteMigrationRunner.Apply(connection, "compression-captures", 1, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, SqlStrings.CreateCompressionCapturesTable);
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.AddCompressionCaptureContentHashColumn);
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.AddCompressionCaptureSeenCountColumn);
            SqliteSchema.AdoptStatement(db, transaction, SqlStrings.AddCompressionCaptureRewriteAcceptedColumn);
            SqliteSchema.Execute(db, transaction, SqlStrings.CreateCompressionCapturesCreatedIndex);
            SqliteSchema.Execute(db, transaction, SqlStrings.CreateCompressionCapturesHashIndex);
        });
    }
}
