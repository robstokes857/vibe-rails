using System.Data;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// VIBE-338: which VibeRails session made each commit, per card it was attached to
/// (<c>board-session-commits/1</c>), and agents' lane-step skips with their reasons
/// (<c>board-agent-skips/1</c>). Both tables are additive; older binaries ignore them.
/// </summary>
public sealed partial class BoardStore
{
    private const string SessionCommitsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSessionCommits (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Sha TEXT NOT NULL,
            SessionId TEXT NOT NULL,
            RepositoryPath TEXT NOT NULL,
            Message TEXT NULL,
            RecordedUTC TEXT NOT NULL,
            DismissedUTC TEXT NULL,
            PRIMARY KEY (CardId, Sha)
        );
        """;

    private const string AgentSkipsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAgentSkips (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            EventId TEXT NOT NULL,
            StepName TEXT NOT NULL,
            Reason TEXT NOT NULL,
            AuthorLabel TEXT NOT NULL,
            AuthorCli TEXT NULL,
            SessionId TEXT NULL,
            CreatedUTC TEXT NOT NULL,
            DismissedUTC TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAgentSkips_Open ON BoardAgentSkips(CardId, CreatedUTC) WHERE DismissedUTC IS NULL;
        CREATE INDEX IF NOT EXISTS IX_BoardAgentSkips_Event ON BoardAgentSkips(EventId);
        """;

    public async Task<IReadOnlyList<string>> RecordSessionCommitAsync(string projectPath, string cardId, string sessionId,
        string sha, string repositoryPath, string? message, CancellationToken cancellationToken = default)
    {
        var normalizedSha = BoardShaText(sha);
        if (string.IsNullOrWhiteSpace(sessionId)) return [];
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return [];
        // The same membership link_board_commit fans out to, read in this writer transaction.
        var targets = await ReadCommitTargetCardsAsync(connection, transaction, project, card.Id, sessionId, cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO BoardSessionCommits (CardId, Sha, SessionId, RepositoryPath, Message, RecordedUTC)
            VALUES ($card, $sha, $session, $repository, $message, $now) ON CONFLICT(CardId, Sha) DO NOTHING;
            """;
        var cardParameter = insert.Parameters.Add("$card", SqliteType.Text);
        insert.Parameters.AddWithValue("$sha", normalizedSha);
        insert.Parameters.AddWithValue("$session", sessionId.Trim());
        insert.Parameters.AddWithValue("$repository", repositoryPath);
        insert.Parameters.AddWithValue("$message", message is null ? DBNull.Value : Bounded(message, 300));
        insert.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        foreach (var target in targets)
        {
            cardParameter.Value = target;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return targets.ToArray();
    }

    public async Task<IReadOnlyList<BoardSessionCommitRecord>> GetUnlinkedSessionCommitsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.Sha, s.SessionId, s.CardId, s.RepositoryPath, s.Message, s.RecordedUTC
            FROM BoardSessionCommits s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.CardId = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
              AND s.DismissedUTC IS NULL
              AND NOT EXISTS (SELECT 1 FROM BoardCommits k WHERE k.CardId = s.CardId AND k.Sha = s.Sha)
            ORDER BY s.RecordedUTC, s.Sha;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        var rows = new List<BoardSessionCommitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), ParseDb(reader.GetString(5))));
        return rows;
    }

    /// <summary>A person unlinked the commit, so it is not a forgotten link.</summary>
    private static async Task DismissSessionCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, string sha, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BoardSessionCommits SET DismissedUTC = $now WHERE CardId = $card AND Sha = $sha AND DismissedUTC IS NULL;";
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$sha", sha);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BoardAgentSkipRecord?> SkipLaunchEventForAgentAsync(string projectPath, string cardId, string eventId,
        string stepName, string reason, BoardAuthor author, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return null;
        await RequireWritableBoardAsync(connection, transaction, card.BoardId, cancellationToken);
        var now = DateTime.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE BoardLaunchEvents SET Verdict = 'skipped', SkipRequested = 1, Summary = $summary
                WHERE Id = $id AND CardId = $card AND Current = 1 AND SkipRequested = 0
                    AND COALESCE(Verdict, '') NOT IN ('passed', 'skipped');
                """;
            update.Parameters.AddWithValue("$summary", BoardLaunchEvent.AgentSkipPrefix + reason);
            update.Parameters.AddWithValue("$id", eventId);
            update.Parameters.AddWithValue("$card", card.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
            update.CommandText = "INSERT INTO BoardLaunchReports(EventId,Verdict,Summary,SessionId,ReviewId,CreatedUTC) VALUES($id,'skipped',$summary,$session,NULL,$now);";
            update.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", ToDb(now));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        var skip = new BoardAgentSkipRecord(NewId("skip"), card.Id, eventId, stepName, reason, author, now);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardAgentSkips (Id, CardId, EventId, StepName, Reason, AuthorLabel, AuthorCli, SessionId, CreatedUTC)
                VALUES ($id, $card, $event, $step, $reason, $label, $cli, $session, $now);
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes)
                VALUES ($comment, $card, $kind, $label, $cli, $session, $body, $now, $rowKind, $changes);
                """;
            insert.Parameters.AddWithValue("$id", skip.Id);
            insert.Parameters.AddWithValue("$card", card.Id);
            insert.Parameters.AddWithValue("$event", eventId);
            insert.Parameters.AddWithValue("$step", stepName);
            insert.Parameters.AddWithValue("$reason", reason);
            insert.Parameters.AddWithValue("$label", author.Label);
            insert.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
            insert.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", ToDb(now));
            insert.Parameters.AddWithValue("$comment", NewId("cm"));
            insert.Parameters.AddWithValue("$kind", author.Kind);
            insert.Parameters.AddWithValue("$body", $"Skipped lane step **{stepName}**. Reason: {reason}");
            insert.Parameters.AddWithValue("$rowKind", BoardCommentKinds.Comment);
            // A lane workflow note: it stays out of Jira and Azure DevOps, like other internal comments.
            insert.Parameters.AddWithValue("$changes", (object?)BoardJiraCommentPolicy.Changes(author, false) ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return skip;
    }

    public async Task<IReadOnlyList<BoardAgentSkipRecord>> GetOpenAgentSkipsAsync(string projectPath, IReadOnlyList<string> cardIds,
        CancellationToken cancellationToken = default)
    {
        if (cardIds.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT k.Id, k.CardId, k.EventId, k.StepName, k.Reason, k.AuthorLabel, k.AuthorCli, k.SessionId, k.CreatedUTC
            FROM BoardAgentSkips k JOIN BoardCards c ON c.Id = k.CardId
            WHERE k.DismissedUTC IS NULL AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
              AND k.CardId IN (SELECT value FROM json_each($cards))
            ORDER BY k.CreatedUTC DESC, k.rowid DESC;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$cards", System.Text.Json.JsonSerializer.Serialize(cardIds.ToList(),
            VibeRails.DTOs.StorageJsonSerializerContext.Default.ListString));
        var rows = new List<BoardAgentSkipRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                BoardAuthor.Agent(reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)),
                ParseDb(reader.GetString(8))));
        return rows;
    }

    public async Task<int> DismissAgentSkipsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BoardAgentSkips SET DismissedUTC = $now WHERE CardId = $card AND DismissedUTC IS NULL;";
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$card", card.Id);
        var dismissed = await command.ExecuteNonQueryAsync(cancellationToken);
        // The tile reads the marker through the card, so the Board poll's change stamp must move.
        if (dismissed > 0) await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dismissed;
    }

    private static string BoardShaText(string sha)
    {
        var trimmed = sha?.Trim().ToLowerInvariant() ?? "";
        if (trimmed.Length is < 7 or > 40 || !trimmed.All(Uri.IsHexDigit))
            throw new BoardValidationException("That does not look like a commit sha.");
        return trimmed;
    }

    private static string Bounded(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
