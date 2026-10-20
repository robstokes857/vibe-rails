using System.Globalization;
using VibeRails.DB;

namespace VibeRails.Data.Sqlite;

/// <summary>Stores prompt-worktree ownership independently of environments and Board cards.</summary>
public sealed class ManagedWorktreeStore : IManagedWorktreeStore
{
    private readonly string _connectionString;

    /// <summary>Uses the normal state database with automatic additive schema setup.</summary>
    public ManagedWorktreeStore(string connectionString)
    {
        _connectionString = connectionString;
        StateDatabaseSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public async Task AddAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ManagedWorktrees (Id, Path, GitCommonDirectory, GitDirectory, Branch, CreatedUTC, DeleteAfterUTC)
            VALUES ($id, $path, $common, $git, $branch, $created, $expiry);
            """;
        command.Parameters.AddWithValue("$id", worktree.Id);
        command.Parameters.AddWithValue("$path", worktree.Path);
        command.Parameters.AddWithValue("$common", worktree.GitCommonDirectory);
        command.Parameters.AddWithValue("$git", worktree.GitDirectory);
        command.Parameters.AddWithValue("$branch", worktree.Branch);
        command.Parameters.AddWithValue("$created", worktree.CreatedUTC.ToString("O"));
        command.Parameters.AddWithValue("$expiry", (object?)worktree.DeleteAfterUTC?.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManagedWorktree>> ListAsync(CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, Path, GitCommonDirectory, GitDirectory, Branch, CreatedUTC, DeleteAfterUTC FROM ManagedWorktrees ORDER BY CreatedUTC;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ManagedWorktree>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ManagedWorktree(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetOpenSessionDirectoriesAsync(CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT DISTINCT WorkingDirectory FROM Sessions WHERE EndedUTC IS NULL;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM ManagedWorktrees WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
}
