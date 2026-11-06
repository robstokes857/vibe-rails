using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static readonly string CodeQualityExemptionsSchemaSql = $"""
        CREATE TABLE IF NOT EXISTS BoardCodeQualityExemptions (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL {(BoardPaths.ProjectPathComparison == StringComparison.OrdinalIgnoreCase ? "COLLATE NOCASE" : "")},
            Path TEXT NOT NULL {(CodeAnalyzerIgnoreRules.PathComparison == StringComparison.OrdinalIgnoreCase ? "COLLATE NOCASE" : "")},
            CardId TEXT NULL,
            EntryJson TEXT NOT NULL,
            RevokedUTC TEXT NULL,
            RevokedByJson TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_CodeQualityExemptions_Repository
            ON BoardCodeQualityExemptions(ProjectPath, Path) WHERE CardId IS NULL AND RevokedUTC IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_CodeQualityExemptions_Card
            ON BoardCodeQualityExemptions(ProjectPath, CardId, Path) WHERE CardId IS NOT NULL AND RevokedUTC IS NULL;
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeQualityExemption>> GetCodeQualityExemptionsAsync(string projectPath,
        string? cardId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // No foreign-key cascade: deleting a card must not erase exemption history.
        command.CommandText = $"""
            SELECT e.EntryJson FROM BoardCodeQualityExemptions e
            WHERE e.ProjectPath=$project{ProjectPathCollation} AND e.RevokedUTC IS NULL
                AND (e.CardId IS NULL OR (e.CardId=$card AND EXISTS (
                    SELECT 1 FROM BoardCards c WHERE c.Id=e.CardId AND c.ProjectPath=e.ProjectPath AND c.DeletedUTC IS NULL)))
            ORDER BY e.Path, e.CardId;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", (object?)cardId ?? DBNull.Value);
        var result = new List<CodeQualityExemption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.CodeQualityExemption)!);
        return result;
    }

    /// <inheritdoc />
    public async Task<CodeQualityExemption> SaveCodeQualityExemptionAsync(string projectPath, string cardId,
        string path, bool permanent, string reason, BoardAuthor author, CancellationToken cancellationToken = default)
    {
        path = ValidateExemptionPath(path);
        reason = reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 2000) throw new BoardValidationException("A reason of 1–2,000 characters is required.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken)
            ?? throw new BoardValidationException("The card no longer exists in this project.");
        await RequireWritableCardAsync(connection, transaction, card.Id, cancellationToken);
        var entry = new CodeQualityExemption(Guid.NewGuid().ToString("N"), path,
            permanent ? null : card.Id, reason, author, DateTime.UtcNow);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE BoardCodeQualityExemptions SET RevokedUTC=$now, RevokedByJson=$author
            WHERE ProjectPath=$project AND Path=$path AND CardId IS $card AND RevokedUTC IS NULL;
            INSERT INTO BoardCodeQualityExemptions(Id,ProjectPath,Path,CardId,EntryJson)
            VALUES($id,$project,$path,$card,$entry);
            """;
        command.Parameters.AddWithValue("$now", ToDb(entry.CreatedUtc));
        command.Parameters.AddWithValue("$author", JsonSerializer.Serialize(author, StorageJsonSerializerContext.Default.BoardAuthor));
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$card", (object?)entry.CardId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry, StorageJsonSerializerContext.Default.CodeQualityExemption));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeCodeQualityExemptionAsync(string projectPath, string cardId, string exemptionId,
        BoardAuthor author, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var card = await ReadCardForWriteAsync(connection, transaction, project, cardId, cancellationToken)
            ?? throw new BoardValidationException("The card no longer exists in this project.");
        await RequireWritableCardAsync(connection, transaction, card.Id, cancellationToken);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE BoardCodeQualityExemptions SET RevokedUTC=$now, RevokedByJson=$author
            WHERE Id=$id AND ProjectPath=$project AND (CardId IS NULL OR CardId=$card) AND RevokedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$author", JsonSerializer.Serialize(author, StorageJsonSerializerContext.Default.BoardAuthor));
        command.Parameters.AddWithValue("$id", exemptionId);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$card", card.Id);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) != 0;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static string ValidateExemptionPath(string path)
    {
        path = (path?.Trim() ?? "").Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length is < 1 or > 1000 || path.StartsWith('/') || path.EndsWith('/')
            || path.Any(char.IsControl) || path.IndexOfAny([':', '*', '?']) >= 0
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new BoardValidationException("Use one exact repository-relative file path, without wildcards or parent traversal.");
        return path;
    }
}
