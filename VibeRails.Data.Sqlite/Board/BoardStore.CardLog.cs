using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// The Card Log (board/14, VB-51): <c>BoardComments</c> rows of kind created / change / deleted,
/// written here in the same transaction as the card write they describe. <c>Changes</c> holds the
/// fields that changed as <c>{"field":{"from":old,"to":new}}</c> (a created entry carries every
/// field as <c>{"to":value}</c>; a description change carries only its new text). <c>Body</c> is
/// a readable one-line summary so older binaries and plain readers need not parse the JSON.
/// <c>RemoteSeq</c> is the sync ledger: NULL not yet sent, -1 retained rejection, 0 local history never sent, &gt;0 the
/// sequence number viberails.ai gave the entry. Older binaries read comment and note rows only.
/// </summary>
public sealed partial class BoardStore
{
    /// <summary>Longest body a change entry keeps; comment and note bodies have their own limit.</summary>
    internal const int MaxLogBodyLength = 2000;

    private const string LogSummarySeparator = " · ";

    private const string CardLogSchemaSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_BoardCards_CardKey ON BoardCards(CardKey) WHERE CardKey IS NOT NULL;
        CREATE INDEX IF NOT EXISTS IX_BoardComments_Unsent ON BoardComments(CardId) WHERE RemoteSeq IS NULL;
        CREATE TABLE IF NOT EXISTS BoardSyncLinks (
            BoardId TEXT PRIMARY KEY REFERENCES Boards(Id) ON DELETE CASCADE,
            RemoteBoardId TEXT NOT NULL,
            Cursor INTEGER NOT NULL DEFAULT 0,
            Enabled INTEGER NOT NULL DEFAULT 1,
            LayoutHash TEXT,
            LastSyncUTC TEXT,
            LastError TEXT,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Appends one Card Log entry inside the caller's write transaction. The caller has already
    /// written the card, so this neither touches nor promotes it.
    /// </summary>
    private static async Task InsertLogEntryAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BoardAuthor author, string kind, string body, string? changes, DateTime createdUtc,
        CancellationToken cancellationToken, BoardSyncStamp? stamp = null)
    {
        // A stamped entry was pulled from viberails.ai: it keeps its remote id and time, and its
        // RemoteSeq is already known, so the push never sends it back.
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes, RemoteSeq)
            VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, $rowKind, $changes, $remoteSeq);
            """;
        insert.Parameters.AddWithValue("$id", stamp?.EntryId ?? NewId("log"));
        insert.Parameters.AddWithValue("$card", cardId);
        insert.Parameters.AddWithValue("$kind", author.Kind);
        insert.Parameters.AddWithValue("$label", author.Label);
        insert.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
        insert.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$body", TruncateLogBody(stamp?.Body ?? body));
        insert.Parameters.AddWithValue("$created", ToDb(stamp?.CreatedUtc ?? createdUtc));
        insert.Parameters.AddWithValue("$rowKind", kind);
        insert.Parameters.AddWithValue("$changes", (object?)(stamp is null ? changes : stamp.Changes ?? changes) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$remoteSeq", stamp is null ? DBNull.Value : stamp.RemoteSeq);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        if (stamp is not null) await ForgetSkippedEntryAsync(connection, transaction, stamp, cancellationToken);
    }

    private static string TruncateLogBody(string body) =>
        body.Length <= MaxLogBodyLength ? body : body[..(MaxLogBodyLength - 1)] + "…";

    /// <summary>The created entry: every synced field, so the entry alone rebuilds the card.</summary>
    private static Task LogCardCreatedAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord card, string laneName, BoardAuthor author, CancellationToken cancellationToken, BoardSyncStamp? stamp = null)
    {
        var log = new CardLogChanges();
        log.Created(card);
        return InsertLogEntryAsync(connection, transaction, card.Id, author, BoardCommentKinds.Created,
            "Created in " + laneName, log.ToJson(), card.CreatedUtc, cancellationToken, stamp);
    }

    /// <summary>
    /// A change entry for the fields that differ between <paramref name="before"/> and
    /// <paramref name="after"/>; nothing when none do. Lane names are only for the summary.
    /// </summary>
    private static Task LogCardChangedAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord before, BoardCardRecord after, string? fromLaneName, string? toLaneName, BoardAuthor author,
        CancellationToken cancellationToken, BoardSyncStamp? stamp = null)
    {
        var log = new CardLogChanges();
        log.Diff(before, after, fromLaneName, toLaneName);
        return log.IsEmpty && stamp is null
            ? Task.CompletedTask
            : InsertLogEntryAsync(connection, transaction, after.Id, author, BoardCommentKinds.Change,
                log.Summary, log.ToJson(), after.UpdatedUtc, cancellationToken, stamp);
    }

    /// <summary>A lane-only change entry: a move between lanes, or a deleted lane's cards falling left.</summary>
    private static Task LogCardMovedAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BoardColumnRecord from, BoardColumnRecord to, BoardAuthor author, DateTime createdUtc,
        CancellationToken cancellationToken)
    {
        var log = new CardLogChanges();
        log.Lane(from.Id, to.Id, from.Name, to.Name);
        return InsertLogEntryAsync(connection, transaction, cardId, author, BoardCommentKinds.Change,
            log.Summary, log.ToJson(), createdUtc, cancellationToken);
    }

    private static Task LogCardDeletedAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BoardAuthor author, DateTime createdUtc, CancellationToken cancellationToken, BoardSyncStamp? stamp = null) =>
        InsertLogEntryAsync(connection, transaction, cardId, author, BoardCommentKinds.Deleted,
            "Deleted", null, createdUtc, cancellationToken, stamp);

    /// <summary>Server-ordered card history, followed by local entries in insertion order.</summary>
    private static async Task<IReadOnlyList<BoardCommentRecord>> ReadHistoryAsync(SqliteConnection connection,
        string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes
            FROM BoardComments
            WHERE CardId = $card AND Kind IN ('{BoardCommentKinds.Created}', '{BoardCommentKinds.Change}', '{BoardCommentKinds.Deleted}')
            ORDER BY CASE WHEN RemoteSeq > 0 THEN 0 ELSE 1 END,
                CASE WHEN RemoteSeq > 0 THEN RemoteSeq END, rowid;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        var entries = new List<BoardCommentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new BoardCommentRecord(
                reader.GetString(0),
                reader.GetString(1),
                new BoardAuthor(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(6),
                ParseDb(reader.GetString(7)),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return entries;
    }

    /// <summary>
    /// Builds one entry's <c>Changes</c> JSON and its readable summary together, so the two can
    /// never disagree about which fields changed. Field names and value types are the sync
    /// contract's: title, description, type, priority, points, assignee, tags, blocked, flagged, lane.
    /// </summary>
    private sealed class CardLogChanges
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly Utf8JsonWriter _json;
        private readonly List<string> _summary = [];
        private int _fields;

        public CardLogChanges()
        {
            _json = new Utf8JsonWriter(_buffer);
            _json.WriteStartObject();
        }

        public bool IsEmpty => _fields == 0;

        public string Summary => string.Join(LogSummarySeparator, _summary);

        public string ToJson()
        {
            _json.WriteEndObject();
            _json.Flush();
            return Encoding.UTF8.GetString(_buffer.WrittenSpan);
        }

        public void Created(BoardCardRecord card)
        {
            WriteTo("displayId", w => w.WriteStringValue(card.DisplayId));
            WriteTo("title", w => w.WriteStringValue(card.Title));
            WriteTo("description", w => w.WriteStringValue(card.Description));
            WriteTo("type", w => w.WriteStringValue(card.Type));
            WriteTo("priority", w => w.WriteStringValue(card.Priority));
            WriteTo("points", w => WritePoints(w, card.Points));
            WriteTo("assignee", w => WriteNullableString(w, card.Assignee));
            WriteTo("tags", w => WriteTags(w, card.Tags));
            WriteTo("blocked", w => w.WriteBooleanValue(card.Blocked));
            WriteTo("flagged", w => w.WriteBooleanValue(card.Flagged));
            WriteTo("lane", w => w.WriteStringValue(card.ColumnId));
        }

        public void Diff(BoardCardRecord before, BoardCardRecord after, string? fromLaneName, string? toLaneName)
        {
            if (before.DisplayId != after.DisplayId)
            {
                WriteFromTo("displayId", w => w.WriteStringValue(before.DisplayId), w => w.WriteStringValue(after.DisplayId));
                _summary.Add($"Display ID: {before.DisplayId} → {after.DisplayId}");
            }
            if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
            {
                WriteFromTo("title", w => w.WriteStringValue(before.Title), w => w.WriteStringValue(after.Title));
                _summary.Add($"Title: {before.Title} → {after.Title}");
            }
            if (!string.Equals(before.Description, after.Description, StringComparison.Ordinal))
            {
                // The new text only: descriptions run to 100,000 characters, and the previous
                // version is the previous entry's "to".
                WriteTo("description", w => w.WriteStringValue(after.Description));
                _summary.Add(after.Description.Length == 0 ? "Description cleared" : "Description edited");
            }
            if (!string.Equals(before.Type, after.Type, StringComparison.Ordinal))
            {
                WriteFromTo("type", w => w.WriteStringValue(before.Type), w => w.WriteStringValue(after.Type));
                _summary.Add($"Type: {before.Type} → {after.Type}");
            }
            if (!string.Equals(before.Priority, after.Priority, StringComparison.Ordinal))
            {
                WriteFromTo("priority", w => w.WriteStringValue(before.Priority), w => w.WriteStringValue(after.Priority));
                _summary.Add($"Priority: {before.Priority} → {after.Priority}");
            }
            if (before.Points != after.Points)
            {
                WriteFromTo("points", w => WritePoints(w, before.Points), w => WritePoints(w, after.Points));
                _summary.Add($"Points: {Show(before.Points)} → {Show(after.Points)}");
            }
            if (!string.Equals(before.Assignee, after.Assignee, StringComparison.Ordinal))
            {
                WriteFromTo("assignee", w => WriteNullableString(w, before.Assignee), w => WriteNullableString(w, after.Assignee));
                _summary.Add($"Assignee: {Show(before.Assignee)} → {Show(after.Assignee)}");
            }
            if (!before.Tags.SequenceEqual(after.Tags, StringComparer.Ordinal))
            {
                WriteFromTo("tags", w => WriteTags(w, before.Tags), w => WriteTags(w, after.Tags));
                _summary.Add($"Tags: {Show(before.Tags)} → {Show(after.Tags)}");
            }
            if (before.Blocked != after.Blocked)
            {
                WriteFromTo("blocked", w => w.WriteBooleanValue(before.Blocked), w => w.WriteBooleanValue(after.Blocked));
                _summary.Add(after.Blocked ? "Blocked" : "Unblocked");
            }
            if (before.Flagged != after.Flagged)
            {
                WriteFromTo("flagged", w => w.WriteBooleanValue(before.Flagged), w => w.WriteBooleanValue(after.Flagged));
                _summary.Add(after.Flagged ? "Flagged" : "Unflagged");
            }
            if (!string.Equals(before.ColumnId, after.ColumnId, StringComparison.Ordinal))
                Lane(before.ColumnId, after.ColumnId, fromLaneName ?? before.ColumnId, toLaneName ?? after.ColumnId);
        }

        public void Lane(string fromId, string toId, string fromName, string toName)
        {
            WriteFromTo("lane", w => w.WriteStringValue(fromId), w => w.WriteStringValue(toId));
            _summary.Add($"Moved {fromName} → {toName}");
        }

        private void WriteTo(string field, Action<Utf8JsonWriter> to)
        {
            _json.WriteStartObject(field);
            _json.WritePropertyName("to");
            to(_json);
            _json.WriteEndObject();
            _fields++;
        }

        private void WriteFromTo(string field, Action<Utf8JsonWriter> from, Action<Utf8JsonWriter> to)
        {
            _json.WriteStartObject(field);
            _json.WritePropertyName("from");
            from(_json);
            _json.WritePropertyName("to");
            to(_json);
            _json.WriteEndObject();
            _fields++;
        }

        private static void WritePoints(Utf8JsonWriter writer, int? points)
        {
            if (points is int value) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }

        private static void WriteNullableString(Utf8JsonWriter writer, string? value)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }

        private static void WriteTags(Utf8JsonWriter writer, IReadOnlyList<string> tags)
        {
            writer.WriteStartArray();
            foreach (var tag in tags) writer.WriteStringValue(tag);
            writer.WriteEndArray();
        }

        private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";
        private static string Show(string? value) => string.IsNullOrEmpty(value) ? "none" : value;
        private static string Show(IReadOnlyList<string> tags) => tags.Count == 0 ? "none" : string.Join(", ", tags);
    }
}
