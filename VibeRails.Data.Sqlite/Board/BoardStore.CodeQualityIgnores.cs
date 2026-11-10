using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static readonly string CodeQualityIgnoresSchemaSql = $"""
        CREATE TABLE IF NOT EXISTS BoardCodeQualityIgnores (
            RepositoryPath TEXT NOT NULL COLLATE NOCASE,
            Path TEXT NOT NULL {(CodeAnalyzerIgnoreRules.PathComparison == StringComparison.OrdinalIgnoreCase ? "COLLATE NOCASE" : "")},
            EntryJson TEXT NOT NULL,
            RemovedUTC TEXT NULL,
            LocallyModified INTEGER NOT NULL DEFAULT 0,
            LegacyFingerprint TEXT NULL,
            PRIMARY KEY (RepositoryPath, Path)
        );
        CREATE TABLE IF NOT EXISTS BoardCodeQualityIgnoreHistory (
            Id TEXT PRIMARY KEY,
            RepositoryPath TEXT NOT NULL COLLATE NOCASE,
            Path TEXT NOT NULL {(CodeAnalyzerIgnoreRules.PathComparison == StringComparison.OrdinalIgnoreCase ? "COLLATE NOCASE" : "")},
            Operation TEXT NOT NULL,
            EntryJson TEXT NOT NULL,
            Author TEXT NULL,
            RecordedUTC TEXT NOT NULL
        );
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeAnalyzerIgnoredFile>> GetCodeQualityIgnoresAsync(string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var repository = CodeAnalyzerIgnoreRules.NormalizeRepository(repositoryPath);
        await RetainLegacyCodeQualityIgnoresAsync(repository, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EntryJson FROM BoardCodeQualityIgnores WHERE RepositoryPath=$repo AND RemovedUTC IS NULL ORDER BY Path;";
        command.Parameters.AddWithValue("$repo", repository);
        var result = new List<CodeAnalyzerIgnoredFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ParseIgnore(reader.GetString(0)));
        return result;
    }

    /// <inheritdoc />
    public async Task SaveCodeQualityIgnoresAsync(string repositoryPath, IReadOnlyList<CodeAnalyzerIgnoredFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return;
        var repository = CodeAnalyzerIgnoreRules.NormalizeRepository(repositoryPath);
        await RetainLegacyCodeQualityIgnoresAsync(repository, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            var normalized = file with
            {
                Path = CodeAnalyzerIgnoreRules.NormalizePath(file.Path),
                MatchKind = string.Equals(file.MatchKind?.Trim(), CodeAnalyzerIgnoreMatchKind.Directory, StringComparison.OrdinalIgnoreCase)
                    ? CodeAnalyzerIgnoreMatchKind.Directory : CodeAnalyzerIgnoreMatchKind.File,
                Author = file.Author ?? Environment.UserName
            };
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT EntryJson FROM BoardCodeQualityIgnores WHERE RepositoryPath=$repo AND Path=$path AND RemovedUTC IS NULL;";
                existing.Parameters.AddWithValue("$repo", repository); existing.Parameters.AddWithValue("$path", normalized.Path);
                if (await existing.ExecuteScalarAsync(cancellationToken) is string previous)
                    normalized = normalized with { CreatedUtc = ParseIgnore(previous).CreatedUtc };
            }
            var json = IgnoreJson(normalized);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO BoardCodeQualityIgnores(RepositoryPath,Path,EntryJson,LocallyModified)
                VALUES($repo,$path,$json,1)
                ON CONFLICT(RepositoryPath,Path) DO UPDATE SET EntryJson=excluded.EntryJson,RemovedUTC=NULL,LocallyModified=1;
                """;
            command.Parameters.AddWithValue("$repo", repository); command.Parameters.AddWithValue("$path", normalized.Path);
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await WriteIgnoreHistoryAsync(connection, transaction, Guid.NewGuid().ToString("N"), repository,
                normalized.Path, "saved", json, normalized.Author, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveCodeQualityIgnoreAsync(string repositoryPath, string path, string author,
        CancellationToken cancellationToken = default)
    {
        var repository = CodeAnalyzerIgnoreRules.NormalizeRepository(repositoryPath);
        path = CodeAnalyzerIgnoreRules.NormalizePath(path);
        await RetainLegacyCodeQualityIgnoresAsync(repository, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EntryJson FROM BoardCodeQualityIgnores WHERE RepositoryPath=$repo AND Path=$path AND RemovedUTC IS NULL;";
        command.Parameters.AddWithValue("$repo", repository); command.Parameters.AddWithValue("$path", path);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string json) return false;
        command.CommandText = "UPDATE BoardCodeQualityIgnores SET RemovedUTC=$now,LocallyModified=1 WHERE RepositoryPath=$repo AND Path=$path;";
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteIgnoreHistoryAsync(connection, transaction, Guid.NewGuid().ToString("N"), repository, path,
            "removed", json, author, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// How long one process trusts its last look at a repository's legacy state.db ignore rows
    /// (VIBE-276). Every Rules scan, Code quality check and pre-commit MintLint step reads the
    /// list; only the first read in each window queries state.db. An older version's later edit
    /// is adopted on the first read after the window.
    /// </summary>
    internal static readonly TimeSpan LegacyIgnoreObservationInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _legacyIgnoresObservedUtc =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clock for <see cref="LegacyIgnoreObservationInterval"/>; tests replace it.</summary>
    internal TimeProvider LegacyIgnoreClock { get; set; } = TimeProvider.System;

    // A data backup (null repository) always reads every repository's legacy rows. A failed read
    // records nothing, so the next call tries again.
    private async Task RetainLegacyCodeQualityIgnoresAsync(string? repository, CancellationToken ct)
    {
        var started = LegacyIgnoreClock.GetUtcNow();
        if (repository is not null && _legacyIgnoresObservedUtc.TryGetValue(repository, out var observed)
            && started >= observed && started - observed < LegacyIgnoreObservationInterval)
            return;
        await AdoptLegacyCodeQualityIgnoresAsync(repository, ct);
        if (repository is not null)
            _legacyIgnoresObservedUtc[repository] = started;
    }

    // Read-only adoption: old binaries retain their own list. Once edited here, the new-side
    // choice wins, but every distinct old-side value remains in the backup history as well.
    // The board.db write transaction opens only when the old side differs from what is adopted.
    private async Task AdoptLegacyCodeQualityIgnoresAsync(string? repository, CancellationToken ct)
    {
        var legacy = new List<(string Repository, CodeAnalyzerIgnoredFile File)>();
        await using (var state = await OpenStateAsync(ct))
        {
            if (!await _stateFeatures.HasTableAsync(state, "CodeAnalyzerIgnores", ct)) return;
            var matchKind = _stateFeatures.HasColumn(state, "CodeAnalyzerIgnores", "MatchKind") ? "MatchKind" : "'file'";
            using var command = state.CreateCommand();
            command.CommandText = $"SELECT RepositoryPath,Path,{matchKind},ReasonKind,ReasonText,CreatedUTC FROM CodeAnalyzerIgnores"
                + (repository is null ? ";" : " WHERE RepositoryPath=$repo;");
            if (repository is not null) command.Parameters.AddWithValue("$repo", repository);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                legacy.Add((reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))));
        }
        await using var connection = await OpenAsync(ct);
        if (!await LegacyIgnoreAdoptionPendingAsync(connection, repository, legacy, ct)) return;
        using var transaction = connection.BeginTransaction();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (legacyRepository, file) in legacy)
        {
            var json = IgnoreJson(file);
            var fingerprint = LegacyIgnoreId(legacyRepository, file.Path, json);
            present.Add(LegacyIgnoreKey(legacyRepository, file.Path));
            // Immutable old-side observations include conflicts with a locally authored choice.
            await WriteIgnoreHistoryAsync(connection, transaction, fingerprint, legacyRepository, file.Path,
                "legacy observed", json, null, ct);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO BoardCodeQualityIgnores(RepositoryPath,Path,EntryJson,LegacyFingerprint)
                VALUES($repo,$path,$json,$fingerprint)
                ON CONFLICT(RepositoryPath,Path) DO UPDATE SET EntryJson=excluded.EntryJson,RemovedUTC=NULL,LegacyFingerprint=excluded.LegacyFingerprint
                WHERE BoardCodeQualityIgnores.LocallyModified=0
                    AND (BoardCodeQualityIgnores.LegacyFingerprint IS NOT excluded.LegacyFingerprint OR BoardCodeQualityIgnores.RemovedUTC IS NOT NULL);
                """;
            command.Parameters.AddWithValue("$repo", legacyRepository); command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$fingerprint", fingerprint);
            await command.ExecuteNonQueryAsync(ct);
        }
        // Old-side deletions may update untouched adopted rules; never resurrect a local removal.
        var removed = new List<(string Repository, string Path, string Json)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT RepositoryPath,Path,EntryJson FROM BoardCodeQualityIgnores WHERE LocallyModified=0 AND RemovedUTC IS NULL"
                + (repository is null ? ";" : " AND RepositoryPath=$repo;");
            if (repository is not null) command.Parameters.AddWithValue("$repo", repository);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!present.Contains(LegacyIgnoreKey(reader.GetString(0), reader.GetString(1))))
                    removed.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var entry in removed)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE BoardCodeQualityIgnores SET RemovedUTC=$now WHERE RepositoryPath=$repo AND Path=$path;";
            command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            command.Parameters.AddWithValue("$repo", entry.Repository); command.Parameters.AddWithValue("$path", entry.Path);
            await command.ExecuteNonQueryAsync(ct);
            await WriteIgnoreHistoryAsync(connection, transaction, LegacyIgnoreId(entry.Repository, entry.Path, "removed:" + entry.Json),
                entry.Repository, entry.Path, "legacy removed", entry.Json, null, ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>Whether adoption would write anything, decided with plain reads. Mirrors the writes:
    /// an old-side value not yet archived, a missing row, an untouched row that differs from the old
    /// side or was removed, or an untouched live row the old side no longer has.</summary>
    private static async Task<bool> LegacyIgnoreAdoptionPendingAsync(SqliteConnection connection, string? repository,
        IReadOnlyList<(string Repository, CodeAnalyzerIgnoredFile File)> legacy, CancellationToken ct)
    {
        var rows = new Dictionary<string, (string? Fingerprint, bool Local, bool Removed)>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT RepositoryPath,Path,LegacyFingerprint,LocallyModified,RemovedUTC IS NOT NULL FROM BoardCodeQualityIgnores"
                + (repository is null ? ";" : " WHERE RepositoryPath=$repo;");
            if (repository is not null) command.Parameters.AddWithValue("$repo", repository);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows[LegacyIgnoreKey(reader.GetString(0), reader.GetString(1))] =
                    (reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4) != 0);
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        var archived = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (legacyRepository, file) in legacy)
        {
            var key = LegacyIgnoreKey(legacyRepository, file.Path);
            var fingerprint = LegacyIgnoreId(legacyRepository, file.Path, IgnoreJson(file));
            present.Add(key);
            archived.Add(fingerprint);
            if (!rows.TryGetValue(key, out var row)) return true;
            if (!row.Local && (row.Fingerprint != fingerprint || row.Removed)) return true;
        }
        if (rows.Any(pair => !pair.Value.Local && !pair.Value.Removed && !present.Contains(pair.Key))) return true;
        if (archived.Count == 0) return false;
        using var history = connection.CreateCommand();
        history.CommandText = "SELECT COUNT(*) FROM BoardCodeQualityIgnoreHistory WHERE Id IN (SELECT value FROM json_each($ids));";
        history.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(archived.ToList(), StorageJsonSerializerContext.Default.ListString));
        return Convert.ToInt64(await history.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != archived.Count;
    }

    private static string LegacyIgnoreKey(string repository, string path) => repository.ToUpperInvariant() + "\0"
        + (CodeAnalyzerIgnoreRules.PathComparison == StringComparison.OrdinalIgnoreCase ? path.ToUpperInvariant() : path);
    private static string LegacyIgnoreId(string repository, string path, string json) =>
        "legacy-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LegacyIgnoreKey(repository, path) + "\0" + json)));
    private static string IgnoreJson(CodeAnalyzerIgnoredFile file) => JsonSerializer.Serialize(file, StorageJsonSerializerContext.Default.CodeAnalyzerIgnoredFile);
    private static CodeAnalyzerIgnoredFile ParseIgnore(string json) => JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.CodeAnalyzerIgnoredFile)!;

    private static async Task WriteIgnoreHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, string repository, string path, string operation, string json, string? author, CancellationToken ct)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardCodeQualityIgnoreHistory(Id,RepositoryPath,Path,Operation,EntryJson,Author,RecordedUTC)
            VALUES($id,$repo,$path,$operation,$json,$author,$now);
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$repo", repository);
        command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$author", (object?)author ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }
}
