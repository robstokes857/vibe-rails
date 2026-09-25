using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<BoardJiraConnectionRecord?> GetJiraConnectionAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = JiraConnectionSelect + " WHERE ProjectPath = $project AND BoardId = $board;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", boardId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJiraConnection(reader) : null;
    }

    public async Task<IReadOnlyList<BoardJiraConnectionRecord>> GetJiraConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A row whose board is gone (saved before board/13's delete trigger) is never pulled.
        command.CommandText = JiraConnectionSelect
            + " WHERE EXISTS (SELECT 1 FROM Boards b WHERE b.Id = BoardJiraConnections.BoardId) ORDER BY ProjectPath, BoardId;";
        var list = new List<BoardJiraConnectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(ReadJiraConnection(reader));
        return list;
    }

    public async Task<BoardJiraConnectionRecord?> SaveJiraConnectionAsync(BoardJiraConnectionRecord connection, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        // The board check and the write are one statement, so a board deleted concurrently cannot
        // be left with a connection. Id is updated too: a new site gets a new link namespace.
        command.CommandText = $"""
            INSERT INTO BoardJiraConnections
                (Id, ProjectPath, BoardId, SiteUrl, Email, HasToken, AuthStatus, StoryPointsFieldId,
                 Jql, Enabled, DisabledReason, OverflowColumnId, LastTestedUTC, LastPullUTC, LastReport)
            SELECT
                $id, $project, $board, $site, $email, $hasToken, $auth, $pointsField,
                $jql, $enabled, $reason, $overflow, $tested, $pulled, $report
            WHERE EXISTS (SELECT 1 FROM Boards WHERE Id = $board AND ProjectPath = $project{ProjectPathCollation})
            ON CONFLICT(ProjectPath, BoardId) DO UPDATE SET
                Id = excluded.Id,
                SiteUrl = excluded.SiteUrl, Email = excluded.Email, HasToken = excluded.HasToken,
                AuthStatus = excluded.AuthStatus, StoryPointsFieldId = excluded.StoryPointsFieldId,
                Jql = excluded.Jql, Enabled = excluded.Enabled, DisabledReason = excluded.DisabledReason,
                OverflowColumnId = excluded.OverflowColumnId, LastTestedUTC = excluded.LastTestedUTC,
                LastPullUTC = excluded.LastPullUTC, LastReport = excluded.LastReport;
            """;
        command.Parameters.AddWithValue("$id", connection.Id);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(connection.ProjectPath));
        command.Parameters.AddWithValue("$board", connection.BoardId);
        command.Parameters.AddWithValue("$site", connection.SiteUrl);
        command.Parameters.AddWithValue("$email", connection.Email);
        command.Parameters.AddWithValue("$hasToken", connection.HasToken ? 1 : 0);
        command.Parameters.AddWithValue("$auth", connection.AuthStatus);
        command.Parameters.AddWithValue("$pointsField", (object?)connection.StoryPointsFieldId ?? DBNull.Value);
        command.Parameters.AddWithValue("$jql", connection.Jql);
        command.Parameters.AddWithValue("$enabled", connection.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)connection.DisabledReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$overflow", (object?)connection.OverflowColumnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tested", connection.LastTestedUtc is DateTime tested ? ToDb(tested) : DBNull.Value);
        command.Parameters.AddWithValue("$pulled", connection.LastPullUtc is DateTime pulled ? ToDb(pulled) : DBNull.Value);
        command.Parameters.AddWithValue("$report", (object?)connection.LastReport ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            return null;
        return await GetJiraConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken);
    }

    public async Task<BoardCardRecord?> CreateJiraCardAsync(
        string projectPath, NewBoardCard card, BoardJiraLinkRecord link, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM BoardJiraLinks WHERE SiteId = $site AND IssueId = $issue;";
            exists.Parameters.AddWithValue("$site", link.SiteId);
            exists.Parameters.AddWithValue("$issue", link.IssueId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                return null;
        }

        var created = await InsertCardAsync(connection, transaction, project, card, cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardJiraLinks (CardId, SiteId, IssueId, IssueKey, AssigneeDisplay, IssueUpdated, LastPulledUTC)
                VALUES ($card, $site, $issue, $key, $assignee, $updated, $pulled);
                """;
            BindLink(insert, link with { CardId = created.Id });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<BoardJiraLinkRecord?> FindJiraLinkAsync(string siteId, string issueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CardId, SiteId, IssueId, IssueKey, AssigneeDisplay, IssueUpdated, LastPulledUTC
            FROM BoardJiraLinks WHERE SiteId = $site AND IssueId = $issue;
            """;
        command.Parameters.AddWithValue("$site", siteId);
        command.Parameters.AddWithValue("$issue", issueId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJiraLink(reader) : null;
    }

    public async Task AddJiraLinkAsync(BoardJiraLinkRecord link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BoardJiraLinks (CardId, SiteId, IssueId, IssueKey, AssigneeDisplay, IssueUpdated, LastPulledUTC)
            VALUES ($card, $site, $issue, $key, $assignee, $updated, $pulled);
            """;
        BindLink(command, link);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateJiraLinkAsync(BoardJiraLinkRecord link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardJiraLinks SET IssueKey = $key, AssigneeDisplay = $assignee,
                IssueUpdated = $updated, LastPulledUTC = $pulled
            WHERE SiteId = $site AND IssueId = $issue;
            """;
        BindLink(command, link);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string JiraConnectionSelect = """
        SELECT Id, ProjectPath, BoardId, SiteUrl, Email, HasToken, AuthStatus, StoryPointsFieldId,
               Jql, Enabled, DisabledReason, OverflowColumnId, LastTestedUTC, LastPullUTC, LastReport
        FROM BoardJiraConnections
        """;

    private static void BindLink(SqliteCommand command, BoardJiraLinkRecord link)
    {
        command.Parameters.AddWithValue("$card", link.CardId);
        command.Parameters.AddWithValue("$site", link.SiteId);
        command.Parameters.AddWithValue("$issue", link.IssueId);
        command.Parameters.AddWithValue("$key", link.IssueKey);
        command.Parameters.AddWithValue("$assignee", (object?)link.AssigneeDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", ToDb(link.IssueUpdated));
        command.Parameters.AddWithValue("$pulled", ToDb(link.LastPulledUtc));
    }

    private static BoardJiraConnectionRecord ReadJiraConnection(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetInt64(5) != 0, reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8), reader.GetInt64(9) != 0,
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : ParseDb(reader.GetString(12)),
        reader.IsDBNull(13) ? null : ParseDb(reader.GetString(13)),
        reader.IsDBNull(14) ? null : reader.GetString(14));

    private static BoardJiraLinkRecord ReadJiraLink(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        ParseDb(reader.GetString(5)), ParseDb(reader.GetString(6)));

    /// <summary>board/12: one Jira connection per board, and one link row per mirrored issue.</summary>
    internal const string JiraSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardJiraConnections (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            BoardId TEXT NOT NULL,
            SiteUrl TEXT NOT NULL,
            Email TEXT NOT NULL DEFAULT '',
            HasToken INTEGER NOT NULL DEFAULT 0,
            AuthStatus TEXT NOT NULL DEFAULT 'none',
            StoryPointsFieldId TEXT NULL,
            Jql TEXT NOT NULL DEFAULT '',
            Enabled INTEGER NOT NULL DEFAULT 0,
            DisabledReason TEXT NULL,
            OverflowColumnId TEXT NULL,
            LastTestedUTC TEXT NULL,
            LastPullUTC TEXT NULL,
            LastReport TEXT NULL,
            UNIQUE(ProjectPath, BoardId)
        );

        CREATE TABLE IF NOT EXISTS BoardJiraLinks (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            SiteId TEXT NOT NULL,
            IssueId TEXT NOT NULL,
            IssueKey TEXT NOT NULL,
            AssigneeDisplay TEXT NULL,
            IssueUpdated TEXT NOT NULL,
            LastPulledUTC TEXT NOT NULL,
            UNIQUE(SiteId, IssueId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardJiraLinks_Card ON BoardJiraLinks(CardId);
        """;

    /// <summary>
    /// board/13: deleting a board deletes its Jira connection, whichever binary deletes it. A
    /// trigger rather than a foreign key, because board/12 already shipped the table and adding
    /// a foreign key would mean rebuilding it. The token file is pruned by the next scheduled pull.
    /// </summary>
    internal const string JiraBoardDeleteTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS Boards_DeleteJiraConnection AFTER DELETE ON Boards BEGIN
            DELETE FROM BoardJiraConnections WHERE BoardId = OLD.Id;
        END;
        """;
}
