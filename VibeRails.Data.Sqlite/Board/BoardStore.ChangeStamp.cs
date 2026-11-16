using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <summary>
    /// board-change-stamp/1 (VIBE-322): every table the Board poll (boards, lanes, card pages) or the
    /// card editor's refresh reads. Each one gets insert, update and delete triggers that rewrite the
    /// one-row <c>BoardChangeStamp</c> with a random value, so every writer moves it: this build,
    /// another root, an older binary, and Jira, Azure DevOps or hosted sync. Frozen with the migration:
    /// a table added later that a tile or the editor shows needs a new migration with its own triggers.
    /// </summary>
    internal static readonly IReadOnlyList<string> ChangeStampTables =
    [
        "Boards", "BoardColumns", "BoardCards", "BoardComments", "BoardDeletedComments", "BoardCardOptions",
        "BoardProjectKeys", "BoardJiraLinks", "BoardAdoLinks", "BoardJiraConnections", "BoardAdoConnections",
        "BoardSharedOrigins", "BoardCommits", "BoardCommitSnapshots", "BoardCardSessions",
        "BoardAdditionalCardSessions", "BoardAttachments", "BoardCodeLinks", "BoardCardLinks", "BoardHandoffs",
        "BoardJiraDeliveries", "BoardAdoDeliveries", "BoardReviews", "BoardContextSamples",
    ];

    /// <summary>
    /// board.db tables whose writes never change what the poll or the card editor's refresh shows:
    /// hot leases and queues, ledgers, caches, and data other views fetch on their own. Their writes
    /// must not move the stamp, or an idle Board would refetch everything every round.
    /// <c>BoardChangeStampTests</c> fails for a table in neither list.
    /// </summary>
    internal static readonly IReadOnlyList<string> UnstampedTables =
    [
        "BoardChangeStamp", "SchemaMigrations", "BoardAgentCompletions", "BoardAppContextSettings",
        "BoardAttachmentContents", "BoardAttentionRequests", "BoardCardSequences", "BoardChecks",
        "BoardCodeQualityExemptions", "BoardCodeQualityIgnoreHistory", "BoardCodeQualityIgnores",
        "BoardContextSettings", "BoardDeletedCards", "BoardDeletedTrackerItems", "BoardDesktopActivity",
        "BoardDisplaySequences", "BoardHistory", "BoardLaneAdditionalAutomations", "BoardLaneAutomationDispatch",
        "BoardLaneAutomations", "BoardLaneStepReports", "BoardLaneWorkflowSteps", "BoardLaneWorkflows",
        "BoardLaunchEvents", "BoardLaunchLanes", "BoardLaunchReports", "BoardLaunchSelections",
        "BoardManagerFeature", "BoardManagerSessions", "BoardManagers", "BoardPendingAdditionalAutomations",
        "BoardPendingAutomations", "BoardRecallEmbeddings", "BoardReviewSettings", "BoardSearchEmbeddings",
        "BoardStarterWorkflows", "BoardSyncEntryOrder", "BoardSyncLinks", "BoardSyncRebuilds",
        "BoardSyncRejectedFields", "BoardSyncSkippedEntries", "BoardSessionCommits",
        // Shown on the tile, but every write also touches the card (BoardStore.SessionCommits.cs).
        "BoardAgentSkips",
    ];

    /// <summary>
    /// Additive: one new table and three triggers per stamped table, writing only that new row. A
    /// random value rather than a counter, so a file restored from a backup cannot reuse a value a
    /// client saw for different content. No existing row is read or rewritten.
    /// </summary>
    private static void ApplyChangeStampMigration(SqliteConnection db, SqliteTransaction transaction)
    {
        var sql = new StringBuilder("""
            CREATE TABLE BoardChangeStamp(Id INTEGER PRIMARY KEY CHECK (Id = 1), Stamp INTEGER NOT NULL);
            INSERT INTO BoardChangeStamp(Id, Stamp) VALUES (1, random());

            """);
        foreach (var table in ChangeStampTables)
            foreach (var (name, operation) in new[] { ("Insert", "INSERT"), ("Update", "UPDATE"), ("Delete", "DELETE") })
                sql.Append(CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER BoardChangeStamp_{table}_{name} AFTER {operation} ON {table} BEGIN
                        UPDATE BoardChangeStamp SET Stamp = random() WHERE Id = 1;
                    END;

                    """);
        SqliteSchema.Execute(db, transaction, sql.ToString());
    }

    public async Task<string?> GetChangeStampAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Stamp FROM BoardChangeStamp WHERE Id = 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is long stamp
            ? stamp.ToString("x16", CultureInfo.InvariantCulture)
            : null;
    }
}
