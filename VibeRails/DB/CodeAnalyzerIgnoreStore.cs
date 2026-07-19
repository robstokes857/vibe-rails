using Microsoft.Data.Sqlite;

namespace VibeRails.DB;

/// <summary>One file the user excluded from Code quality scans in one repository.</summary>
/// <param name="Path">Repository-relative path, forward slashes.</param>
/// <param name="ReasonKind">"test" / "config" / "other", or null when no reason was given.</param>
/// <param name="ReasonText">Optional free-text note (used with "other", but stored for any kind).</param>
public sealed record CodeAnalyzerIgnoredFile(
    string Path,
    string? ReasonKind,
    string? ReasonText,
    DateTime CreatedUtc);

/// <summary>
/// Persists the Code quality ignore list to state.db, keyed per repository so one state.db can
/// serve many checkouts. Unlike the token-saver tally this store THROWS on failure: an ignore
/// the user believes was saved but wasn't would silently reintroduce files into results.
/// </summary>
public interface ICodeAnalyzerIgnoreStore
{
    Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Repository-relative ignored paths, compared case-insensitively.</summary>
    Task<HashSet<string>> GetIgnoredPathsAsync(string repositoryPath, CancellationToken cancellationToken);

    Task UpsertAsync(string repositoryPath, CodeAnalyzerIgnoredFile file, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string repositoryPath, string path, CancellationToken cancellationToken);
}

/// <summary>
/// Connection-per-operation like <see cref="Repository"/>, plus <c>busy_timeout</c> (which
/// <see cref="Repository"/> famously lacks) because this store races background embedding jobs
/// for the same database file. The table is created on demand so the store works even when it
/// runs before the first <see cref="Repository"/> initializes the schema.
/// </summary>
public sealed class CodeAnalyzerIgnoreStore(string connectionString) : ICodeAnalyzerIgnoreStore
{
    public async Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.SelectCodeAnalyzerIgnores;
        command.Parameters.AddWithValue("$repositoryPath", NormalizeRepository(repositoryPath));

        List<CodeAnalyzerIgnoredFile> files = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new CodeAnalyzerIgnoredFile(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                    ? created
                    : DateTime.MinValue));
        }

        return files;
    }

    public async Task<HashSet<string>> GetIgnoredPathsAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var files = await ListAsync(repositoryPath, cancellationToken);
        return files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task UpsertAsync(
        string repositoryPath,
        CodeAnalyzerIgnoredFile file,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.UpsertCodeAnalyzerIgnore;
        command.Parameters.AddWithValue("$repositoryPath", NormalizeRepository(repositoryPath));
        command.Parameters.AddWithValue("$path", NormalizePath(file.Path));
        command.Parameters.AddWithValue("$reasonKind", (object?)file.ReasonKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasonText", (object?)file.ReasonText ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUTC", file.CreatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        string repositoryPath,
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.DeleteCodeAnalyzerIgnore;
        command.Parameters.AddWithValue("$repositoryPath", NormalizeRepository(repositoryPath));
        command.Parameters.AddWithValue("$path", NormalizePath(path));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Repository keys are absolute paths; normalize separators and trailing slash.</summary>
    internal static string NormalizeRepository(string repositoryPath) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repositoryPath))
            .Replace('\\', '/');

    /// <summary>File keys are repository-relative, forward slashes, no leading "./".</summary>
    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateCodeAnalyzerIgnoresTable;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }
}
