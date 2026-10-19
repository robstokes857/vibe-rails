using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<BoardAdoConnectionRecord?> GetAdoConnectionAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoConnectionSelect + " WHERE ProjectPath = $project AND BoardId = $board;";
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$board", boardId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAdoConnection(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardAdoConnectionRecord>> GetAdoConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoConnectionSelect
            + " WHERE EXISTS (SELECT 1 FROM Boards b WHERE b.Id = BoardAdoConnections.BoardId) ORDER BY ProjectPath, BoardId;";
        var list = new List<BoardAdoConnectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(ReadAdoConnection(reader));
        return list;
    }

    /// <inheritdoc />
    public async Task<BoardAdoConnectionRecord?> SaveAdoConnectionAsync(BoardAdoConnectionRecord connection, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        // The board check and the write are one statement, so a board deleted concurrently cannot
        // be left with a connection. Id is updated too: a new organization gets a new link namespace.
        command.CommandText = $"""
            INSERT INTO BoardAdoConnections
                (Id, ProjectPath, BoardId, Organization, Project, Team, BacklogLevel, QueryId, BoardLink, AdoBoardName,
                 ColumnMap, PointsField, NarrowWiql, SkipOldDone, HasToken, AuthStatus, Enabled, DisabledReason,
                 OverflowColumnId, DedicatedBoard, LastTestedUTC, LastPullUTC, LastReport)
            SELECT
                $id, $project, $board, $organization, $adoProject, $team, $backlog, $query, $link, $name,
                $columnMap, $pointsField, $narrow, $skipOldDone, $hasToken, $auth, $enabled, $reason,
                $overflow, $dedicated, $tested, $pulled, $report
            WHERE EXISTS (SELECT 1 FROM Boards WHERE Id = $board AND ProjectPath = $project{ProjectPathCollation})
            ON CONFLICT(ProjectPath, BoardId) DO UPDATE SET
                Id = excluded.Id,
                Organization = excluded.Organization, Project = excluded.Project, Team = excluded.Team,
                BacklogLevel = excluded.BacklogLevel, QueryId = excluded.QueryId, BoardLink = excluded.BoardLink,
                AdoBoardName = excluded.AdoBoardName, ColumnMap = excluded.ColumnMap,
                PointsField = excluded.PointsField, NarrowWiql = excluded.NarrowWiql,
                SkipOldDone = excluded.SkipOldDone, HasToken = excluded.HasToken,
                AuthStatus = excluded.AuthStatus, Enabled = excluded.Enabled,
                DisabledReason = excluded.DisabledReason, OverflowColumnId = excluded.OverflowColumnId,
                DedicatedBoard = excluded.DedicatedBoard, LastTestedUTC = excluded.LastTestedUTC,
                LastPullUTC = excluded.LastPullUTC, LastReport = excluded.LastReport;
            """;
        command.Parameters.AddWithValue("$id", connection.Id);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(connection.ProjectPath));
        command.Parameters.AddWithValue("$board", connection.BoardId);
        command.Parameters.AddWithValue("$organization", connection.Organization);
        command.Parameters.AddWithValue("$adoProject", connection.Project);
        command.Parameters.AddWithValue("$team", (object?)connection.Team ?? DBNull.Value);
        command.Parameters.AddWithValue("$backlog", (object?)connection.BacklogLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)connection.QueryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$link", connection.BoardLink);
        command.Parameters.AddWithValue("$name", (object?)connection.AdoBoardName ?? DBNull.Value);
        command.Parameters.AddWithValue("$columnMap", (object?)connection.ColumnMap ?? DBNull.Value);
        command.Parameters.AddWithValue("$pointsField", (object?)connection.PointsField ?? DBNull.Value);
        command.Parameters.AddWithValue("$narrow", (object?)connection.NarrowWiql ?? DBNull.Value);
        command.Parameters.AddWithValue("$skipOldDone", connection.SkipOldDone is bool skip ? (skip ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$hasToken", connection.HasToken ? 1 : 0);
        command.Parameters.AddWithValue("$auth", connection.AuthStatus);
        command.Parameters.AddWithValue("$enabled", connection.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)connection.DisabledReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$overflow", (object?)connection.OverflowColumnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dedicated", connection.DedicatedBoard ? 1 : DBNull.Value);
        command.Parameters.AddWithValue("$tested", connection.LastTestedUtc is DateTime tested ? ToDb(tested) : DBNull.Value);
        command.Parameters.AddWithValue("$pulled", connection.LastPullUtc is DateTime pulled ? ToDb(pulled) : DBNull.Value);
        command.Parameters.AddWithValue("$report", (object?)connection.LastReport ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            return null;
        return await GetAdoConnectionAsync(connection.ProjectPath, connection.BoardId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAdoConnectionAsync(
        string projectPath, string boardId, string connectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM BoardAdoConnections WHERE ProjectPath = $project{ProjectPathCollation} AND BoardId = $board AND Id = $id;";
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$id", connectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<BoardAdoLinkRecord?> FindAdoLinkAsync(string connectionId, int workItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoLinkSelect + " FROM BoardAdoLinks a WHERE a.ConnectionId = $connection AND a.WorkItemId = $item;";
        command.Parameters.AddWithValue("$connection", connectionId);
        command.Parameters.AddWithValue("$item", workItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAdoLink(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardAdoLinkRecord>> GetAdoLinksForCardAsync(
        string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoLinkSelect + $"""
             FROM BoardAdoLinks a
            JOIN BoardCards c ON c.Id = a.CardId
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ORDER BY a.ConnectionId, a.WorkItemId;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        var links = new List<BoardAdoLinkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(ReadAdoLink(reader));
        return links;
    }

    /// <inheritdoc />
    public async Task<BoardCardRecord?> CreateAdoCardAsync(
        string projectPath, NewBoardCard card, BoardAdoLinkRecord link, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM BoardAdoLinks WHERE ConnectionId = $connection AND WorkItemId = $item;";
            exists.Parameters.AddWithValue("$connection", link.ConnectionId);
            exists.Parameters.AddWithValue("$item", link.WorkItemId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                return null;
        }

        var created = await InsertCardAsync(connection, transaction, project, card, AdoAuthor, cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = AdoLinkInsertSql;
            BindAdoLink(insert, link with { CardId = created.Id });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <inheritdoc />
    public async Task AddAdoLinkAsync(BoardAdoLinkRecord link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoLinkInsertSql;
        BindAdoLink(command, link);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateAdoLinkAsync(BoardAdoLinkRecord link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardAdoLinks SET Rev = $rev, ChangedDate = $changed, AssigneeDisplay = $assignee,
                Mapping = $mapping, LastPulledUTC = $pulled
            WHERE ConnectionId = $connection AND WorkItemId = $item;
            """;
        BindAdoLink(command, link);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The agent every pulled card, and every pull-written comment, is authored by.
    /// The delivery trigger never sends this author's comments back to Azure DevOps.</summary>
    private static readonly BoardAuthor AdoAuthor = BoardAuthor.Agent(AdoAuthorLabel, null, null);
    private const string AdoAuthorLabel = "Azure DevOps";

    private const string AdoConnectionSelect = """
        SELECT Id, ProjectPath, BoardId, Organization, Project, Team, BacklogLevel, QueryId, BoardLink, AdoBoardName,
               ColumnMap, PointsField, NarrowWiql, SkipOldDone, HasToken, AuthStatus, Enabled, DisabledReason,
               OverflowColumnId, DedicatedBoard, LastTestedUTC, LastPullUTC, LastReport
        FROM BoardAdoConnections
        """;

    private const string AdoLinkSelect =
        "SELECT a.CardId, a.ConnectionId, a.WorkItemId, a.Rev, a.ChangedDate, a.AssigneeDisplay, a.LastPulledUTC, a.Mapping";

    private const string AdoLinkInsertSql = """
        INSERT INTO BoardAdoLinks (CardId, ConnectionId, WorkItemId, Rev, ChangedDate, AssigneeDisplay, Mapping, LastPulledUTC)
        VALUES ($card, $connection, $item, $rev, $changed, $assignee, $mapping, $pulled);
        """;

    private static void BindAdoLink(SqliteCommand command, BoardAdoLinkRecord link)
    {
        command.Parameters.AddWithValue("$card", link.CardId);
        command.Parameters.AddWithValue("$connection", link.ConnectionId);
        command.Parameters.AddWithValue("$item", link.WorkItemId);
        command.Parameters.AddWithValue("$rev", link.Rev);
        command.Parameters.AddWithValue("$changed", ToDb(link.ChangedDate));
        command.Parameters.AddWithValue("$assignee", (object?)link.AssigneeDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$mapping", (object?)link.Mapping ?? DBNull.Value);
        command.Parameters.AddWithValue("$pulled", ToDb(link.LastPulledUtc));
    }

    private static BoardAdoConnectionRecord ReadAdoConnection(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4),
        NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7),
        reader.GetString(8), NullableString(reader, 9),
        NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0,
        reader.GetInt64(14) != 0, reader.GetString(15), reader.GetInt64(16) != 0,
        NullableString(reader, 17), NullableString(reader, 18),
        !reader.IsDBNull(19) && reader.GetInt64(19) != 0,
        reader.IsDBNull(20) ? null : ParseDb(reader.GetString(20)),
        reader.IsDBNull(21) ? null : ParseDb(reader.GetString(21)),
        NullableString(reader, 22));

    private static BoardAdoLinkRecord ReadAdoLink(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
        ParseDb(reader.GetString(4)), NullableString(reader, 5), ParseDb(reader.GetString(6)),
        NullableString(reader, 7));

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// board-ado/1: one Azure DevOps connection per board, one link row per mirrored work item, and
    /// the trigger that deletes a board's connection with the board (from any binary). New tables,
    /// never Jira's with a provider column: an older binary reads every <c>BoardJiraConnections</c>
    /// row as Jira, and its delivery trigger would post Azure DevOps comments to Jira. An older
    /// binary ignores these tables. The token is not a column; it stays in <c>ado-tokens.json</c>.
    /// </summary>
    internal const string AdoSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAdoConnections (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            BoardId TEXT NOT NULL,
            Organization TEXT NOT NULL,
            Project TEXT NOT NULL,
            Team TEXT NULL,
            BacklogLevel TEXT NULL,
            QueryId TEXT NULL,
            BoardLink TEXT NOT NULL,
            AdoBoardName TEXT NULL,
            ColumnMap TEXT NULL,
            PointsField TEXT NULL,
            NarrowWiql TEXT NULL,
            SkipOldDone INTEGER NULL,
            HasToken INTEGER NOT NULL DEFAULT 0,
            AuthStatus TEXT NOT NULL DEFAULT 'none',
            Enabled INTEGER NOT NULL DEFAULT 0,
            DisabledReason TEXT NULL,
            OverflowColumnId TEXT NULL,
            DedicatedBoard INTEGER NULL,
            LastTestedUTC TEXT NULL,
            LastPullUTC TEXT NULL,
            LastReport TEXT NULL,
            UNIQUE(ProjectPath, BoardId)
        );

        CREATE TABLE IF NOT EXISTS BoardAdoLinks (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            ConnectionId TEXT NOT NULL,
            WorkItemId INTEGER NOT NULL,
            Rev INTEGER NOT NULL,
            ChangedDate TEXT NOT NULL,
            AssigneeDisplay TEXT NULL,
            Mapping TEXT NULL,
            LastPulledUTC TEXT NOT NULL,
            UNIQUE(ConnectionId, WorkItemId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAdoLinks_Card ON BoardAdoLinks(CardId);

        CREATE TRIGGER IF NOT EXISTS Boards_DeleteAdoConnection AFTER DELETE ON Boards BEGIN
            DELETE FROM BoardAdoConnections WHERE BoardId = OLD.Id;
        END;
        """;
}
