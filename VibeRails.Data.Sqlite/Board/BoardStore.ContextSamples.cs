using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// Agent-context samples (VB-63, board/19). One row per launch measurement; the same
/// transaction appends a Card Log <c>change</c> entry whose <c>Changes</c> carries the numbers
/// under <c>context</c>, so the sample syncs to viberails.ai (the hosted contract keeps unknown
/// change fields verbatim) and shows in History. The card itself is not touched or promoted:
/// a measurement is telemetry, not activity.
/// </summary>
public sealed partial class BoardStore
{
    /// <summary>The <c>Changes</c> field name a sample travels under; never a card field.</summary>
    public const string ContextChangeField = "context";

    private const string ContextSamplesSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardContextSamples (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            SessionId TEXT NULL,
            MeasuredUTC TEXT NOT NULL,
            Intent TEXT NOT NULL,
            Cli TEXT NULL,
            Selection TEXT NULL,
            Tokens INTEGER NOT NULL,
            Chars INTEGER NOT NULL,
            PromptTokens INTEGER NOT NULL,
            CardReadTokens INTEGER NOT NULL,
            Breakdown TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardContextSamples_Card ON BoardContextSamples(CardId, MeasuredUTC);
        """;

    /// <inheritdoc />
    public async Task<BoardContextSampleRecord?> RecordContextSampleAsync(string projectPath, string cardId, NewBoardContextSample sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        var record = new BoardContextSampleRecord(NewId("ctx"), card.Id, sample.SessionId, DateTime.UtcNow, sample.Intent,
            sample.Cli, sample.Selection, sample.Tokens, sample.Chars, sample.PromptTokens, sample.CardReadTokens, sample.BreakdownJson);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardContextSamples (Id, CardId, SessionId, MeasuredUTC, Intent, Cli, Selection, Tokens, Chars, PromptTokens, CardReadTokens, Breakdown)
                VALUES ($id, $card, $session, $measured, $intent, $cli, $selection, $tokens, $chars, $promptTokens, $cardReadTokens, $breakdown);
                """;
            insert.Parameters.AddWithValue("$id", record.Id);
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$session", (object?)record.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$measured", ToDb(record.MeasuredUtc));
            insert.Parameters.AddWithValue("$intent", record.Intent);
            insert.Parameters.AddWithValue("$cli", (object?)record.Cli ?? DBNull.Value);
            insert.Parameters.AddWithValue("$selection", (object?)record.Selection ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tokens", record.Tokens);
            insert.Parameters.AddWithValue("$chars", record.Chars);
            insert.Parameters.AddWithValue("$promptTokens", record.PromptTokens);
            insert.Parameters.AddWithValue("$cardReadTokens", record.CardReadTokens);
            insert.Parameters.AddWithValue("$breakdown", record.BreakdownJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertLogEntryAsync(connection, transaction, card.Id, BoardAuthor.System(), BoardCommentKinds.Change,
            ContextSampleSummary(record), ContextSampleChanges(record), record.MeasuredUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    /// <inheritdoc />
    public async Task<BoardContextSampleRecord?> GetLatestContextSampleAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.Id, s.CardId, s.SessionId, s.MeasuredUTC, s.Intent, s.Cli, s.Selection, s.Tokens, s.Chars, s.PromptTokens, s.CardReadTokens, s.Breakdown
            FROM BoardContextSamples s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.CardId = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ORDER BY s.MeasuredUTC DESC, s.rowid DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new BoardContextSampleRecord(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseDb(reader.GetString(3)), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11));
    }

    /// <summary>The readable History line; older binaries and plain readers see this without parsing JSON.</summary>
    internal static string ContextSampleSummary(BoardContextSampleRecord sample)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"Agent launch context ≈ {sample.Tokens:N0} tokens (prompt {sample.PromptTokens:N0} · card read {sample.CardReadTokens:N0})");
        if (!string.IsNullOrWhiteSpace(sample.Cli)) line += LogSummarySeparator + sample.Cli;
        return line + LogSummarySeparator + sample.Intent;
    }

    /// <summary>
    /// <c>{"context":{"to":{…}}}</c>: the numbers, intent and CLI, plus the breakdown the host
    /// measured. Session ids stay out of it, as everywhere else in the sync payload.
    /// </summary>
    internal static string ContextSampleChanges(BoardContextSampleRecord sample)
    {
        var to = new JsonObject
        {
            ["tokens"] = sample.Tokens,
            ["chars"] = sample.Chars,
            ["prompt"] = sample.PromptTokens,
            ["cardRead"] = sample.CardReadTokens,
            ["intent"] = sample.Intent,
            ["cli"] = sample.Cli,
        };
        try
        {
            if (JsonNode.Parse(sample.BreakdownJson) is { } breakdown)
                to["breakdown"] = breakdown;
        }
        catch (JsonException)
        {
            // A malformed breakdown loses only its detail; the numbers above still sync.
        }
        return new JsonObject { [ContextChangeField] = new JsonObject { ["to"] = to } }.ToJsonString();
    }
}
