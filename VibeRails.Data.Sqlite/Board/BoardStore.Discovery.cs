using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardRecord>> GetLocalBoardsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BoardSelectSql + " ORDER BY ProjectPath, Position, Id;";
        var boards = new List<BoardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) boards.Add(ReadBoard(reader));
        return boards;
    }

    /// <inheritdoc />
    public async Task<BoardCardRecord?> FindLocalCardAsync(string idOrKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadLocalCardAsync(connection, null, idOrKey, cancellationToken);
    }

    private static async Task<BoardCardRecord?> ReadLocalCardAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string idOrKey, CancellationToken cancellationToken)
    {
        var input = idOrKey.Trim();
        var byId = await ReadOneCardAsync(connection, transaction,
            CardSelectSql + " WHERE c.Id = $input AND c.DeletedUTC IS NULL LIMIT 1;",
            [("$input", input)], cancellationToken);
        // Imported shared boards can retain stored legacy keys such as VB-1. Those are
        // project-local aliases too, so only random permanent keys get global resolution.
        if (byId is not null || !BoardKeys.TryParseStored(input, out var key)) return byId;
        return await ReadOneCardAsync(connection, transaction,
            CardSelectSql + " WHERE c.CardKey = $input COLLATE NOCASE AND c.DeletedUTC IS NULL LIMIT 1;",
            [("$input", key)], cancellationToken);
    }
}
