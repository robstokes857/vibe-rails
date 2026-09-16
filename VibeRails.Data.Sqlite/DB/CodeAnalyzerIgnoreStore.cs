using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

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
                NormalizeMatchKind(reader.IsDBNull(1) ? null : reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTime.TryParse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var created)
                    ? created
                    : DateTime.MinValue));
        }

        return files;
    }

    public Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetIgnoreRulesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
        => ListAsync(repositoryPath, cancellationToken);

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
        command.Parameters.AddWithValue("$matchKind", NormalizeMatchKind(file.MatchKind));
        command.Parameters.AddWithValue("$reasonKind", (object?)file.ReasonKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasonText", (object?)file.ReasonText ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUTC", file.CreatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertManyAsync(
        string repositoryPath,
        IReadOnlyList<CodeAnalyzerIgnoredFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlStrings.UpsertCodeAnalyzerIgnore;
        command.Parameters.AddWithValue("$repositoryPath", NormalizeRepository(repositoryPath));
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        var matchKindParam = command.Parameters.Add("$matchKind", SqliteType.Text);
        var reasonKindParam = command.Parameters.Add("$reasonKind", SqliteType.Text);
        var reasonTextParam = command.Parameters.Add("$reasonText", SqliteType.Text);
        var createdParam = command.Parameters.Add("$createdUTC", SqliteType.Text);
        foreach (var file in files)
        {
            pathParam.Value = NormalizePath(file.Path);
            matchKindParam.Value = NormalizeMatchKind(file.MatchKind);
            reasonKindParam.Value = (object?)file.ReasonKind ?? DBNull.Value;
            reasonTextParam.Value = (object?)file.ReasonText ?? DBNull.Value;
            createdParam.Value = file.CreatedUtc.ToString("O");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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

    /// <summary>
    /// Case sensitivity for repository-relative path comparison, following host filesystem
    /// semantics: case-insensitive on Windows and macOS, case-sensitive on Linux. This mirrors how
    /// the checkout itself distinguishes (or conflates) src/Foo.cs and src/foo.cs.
    /// </summary>
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// True if <paramref name="relativePath"/> is excluded by any of <paramref name="rules"/>.
    /// File rules match the exact path; directory rules match the directory path itself plus
    /// everything under it. Comparison follows host filesystem case semantics
    /// (<see cref="PathComparison"/>).
    /// </summary>
    public static bool IsIgnored(string relativePath, IReadOnlyList<CodeAnalyzerIgnoredFile> rules)
    {
        if (rules.Count == 0) return false;
        var normalized = NormalizePath(relativePath);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var rulePath = NormalizePath(rule.Path);
            if (string.Equals(rulePath, normalized, PathComparison))
                return true;
            if (IsDirectoryMatchKind(rule.MatchKind)
                && normalized.StartsWith(rulePath + "/", PathComparison))
                return true;
        }
        return false;
    }

    private static bool IsDirectoryMatchKind(string? matchKind) =>
        string.Equals(matchKind, CodeAnalyzerIgnoreMatchKind.Directory, StringComparison.OrdinalIgnoreCase);

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

        // Directory rules are stored without a trailing slash so they don't accidentally
        // match siblings via prefix logic. The matcher adds the "/" back when comparing.
        return normalized.TrimEnd('/');
    }

    /// <summary>Coerces unknown/null MatchKind values to the default ("file").</summary>
    private static string NormalizeMatchKind(string? matchKind)
    {
        if (string.IsNullOrWhiteSpace(matchKind)) return CodeAnalyzerIgnoreMatchKind.File;
        var lower = matchKind.Trim().ToLowerInvariant();
        return lower is CodeAnalyzerIgnoreMatchKind.File or CodeAnalyzerIgnoreMatchKind.Directory
            ? lower
            : CodeAnalyzerIgnoreMatchKind.File;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
        try
        {
            SqliteMigrationRunner.Apply(connection, "code-analyzer-ignores", 1, (db, transaction) =>
            {
                SqliteSchema.Execute(db, transaction, SqlStrings.CreateCodeAnalyzerIgnoresTable);
                SqliteSchema.AdoptStatement(db, transaction, SqlStrings.MigrateCodeAnalyzerIgnoresAddMatchKind);
            });
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
