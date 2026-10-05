using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using VibeRails.Services.UserInOut;

namespace VibeRails.Data.Sqlite;

public sealed partial class SqliteSearchIndexStore
{
    public async Task<int> ReconcileAsync(IBoardStore board, int batchSize, CancellationToken ct)
    {
        Initialize();
        var total = 0;
        // Independent durable cursors give every corpus progress on every tick, including an empty history queue.
        foreach (var kind in new[] { "board", "input", "session" })
        {
            ct.ThrowIfCancellationRequested();
            var claim = ClaimReconciliation(kind);
            if (claim is null) continue;
            try
            {
                var size = Math.Clamp(batchSize, 1, kind == "session" ? 5 : 25);
                IReadOnlyList<SearchDocument> documents;
                string cursor;
                if (kind == "board")
                {
                    var cards = await board.GetSearchDocumentsAsync(0, ct, afterCardId: claim.Cursor.Length == 0 ? null : claim.Cursor, pageSize: size);
                    documents = cards.Select(card => new SearchDocument(card.Id, "board", card.ProjectPath, card.Sources,
                        Board: card with { Sources = [], Passages = [], KeywordSources = [] })).ToArray();
                    cursor = cards.LastOrDefault()?.Id ?? claim.Cursor;
                }
                else
                {
                    (documents, cursor) = ReadHistoryPage(kind, claim.Cursor, size, ct);
                }
                foreach (var document in documents)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Ingest(document, claim)) break;
                    total++;
                }
                FinishPage(claim, cursor, documents.Count < size);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                using var failed = Open();
                Execute(failed, """
                    UPDATE SearchCheckpoints SET Attempts=Attempts+1,RetryAt=$now+min(3600000,30000*(1<<min(Attempts,7))),Error=$error
                    WHERE Kind=$kind AND Lease=$lease
                    """, ("$now", Now), ("$error", ex.Message[..Math.Min(500, ex.Message.Length)]), ("$kind", kind), ("$lease", claim.Lease));
                Serilog.Log.Warning(ex, "[SearchIndex] {Kind} reconciliation deferred for retry", kind);
            }
            finally
            {
                using var db = Open();
                Execute(db, "UPDATE SearchCheckpoints SET Lease=NULL,LeaseUntil=0 WHERE Kind=$kind AND Lease=$lease",
                    ("$kind", kind), ("$lease", claim.Lease));
            }
        }
        return total;
    }

    private sealed record Reconciliation(string Kind, string Cursor, long Epoch, string Lease);

    private Reconciliation? ClaimReconciliation(string kind)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, "INSERT OR IGNORE INTO SearchCheckpoints(Kind) VALUES($kind)", ("$kind", kind));
        var lease = Guid.NewGuid().ToString("N");
        if (Execute(db, "UPDATE SearchCheckpoints SET Lease=$lease,LeaseUntil=$until WHERE Kind=$kind AND LeaseUntil<=$now AND RetryAt<=$now",
            ("$lease", lease), ("$until", Now + LeaseMilliseconds), ("$kind", kind), ("$now", Now)) != 1) return null;
        using var cmd = Command(db, "SELECT Cursor,Epoch FROM SearchCheckpoints WHERE Kind=$kind", ("$kind", kind));
        Reconciliation claim;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            claim = new(kind, reader.GetString(0), reader.GetInt64(1), lease);
        }
        tx.Commit();
        return claim;
    }

    private bool Ingest(SearchDocument document, Reconciliation claim)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (Execute(db, "UPDATE SearchCheckpoints SET LeaseUntil=$until WHERE Kind=$kind AND Lease=$lease AND LeaseUntil>$now",
            ("$until", Now + LeaseMilliseconds), ("$kind", claim.Kind), ("$lease", claim.Lease), ("$now", Now)) != 1) return false;
        if (document.Sources.Count == 0)
        {
            Execute(db, "DELETE FROM SearchDocuments WHERE Id=$id", ("$id", document.Id));
            tx.Commit();
            return true;
        }
        Execute(db, """
            INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch)
            VALUES($id,$kind,$project,$session,$metadata,$epoch)
            ON CONFLICT(Id) DO UPDATE SET ProjectPath=excluded.ProjectPath,SessionId=excluded.SessionId,
                Metadata=excluded.Metadata,SeenEpoch=excluded.SeenEpoch
            """, ("$id", document.Id), ("$kind", document.Kind), ("$project", document.ProjectPath),
            ("$session", document.Input?.SessionId ?? document.Session?.SessionId),
            ("$metadata", Json(document with { Sources = [] })), ("$epoch", claim.Epoch));
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in document.Sources)
        {
            var id = Hash(document.Id + "\0" + source.Id);
            sourceIds.Add(id);
            var hash = Hash(source.Title + "\0" + source.Text);
            using var existing = Command(db, "SELECT Hash FROM SearchSources WHERE Id=$id", ("$id", id));
            if (existing.ExecuteScalar() as string == hash) continue;
            // Remove obsolete chunks in the same short commit as replacement text. Orphan vec rows
            // cannot join live chunks; maintenance prunes them even when ONNX/vec is unavailable now.
            Execute(db, "DELETE FROM SearchSources WHERE Id=$id", ("$id", id));
            Execute(db, "INSERT INTO SearchSources(Id,DocumentId,SourceKey,Text,Title,Hash) VALUES($id,$doc,$key,$text,$title,$hash)",
                ("$id", id), ("$doc", document.Id), ("$key", source.Id), ("$text", source.Text), ("$title", source.Title), ("$hash", hash));
        }
        using (var stale = Command(db, "SELECT Id FROM SearchSources WHERE DocumentId=$id", ("$id", document.Id)))
        {
            var removed = new List<string>();
            using (var reader = stale.ExecuteReader())
                while (reader.Read()) if (!sourceIds.Contains(reader.GetString(0))) removed.Add(reader.GetString(0));
            foreach (var id in removed) Execute(db, "DELETE FROM SearchSources WHERE Id=$id", ("$id", id));
        }
        if (document.Kind == "session")
            Execute(db, """
                UPDATE SearchDocuments SET Metadata=json_set(Metadata,'$.session.totalChunkCount',
                    (SELECT count(*) FROM SearchChunks c JOIN SearchSources s ON s.Id=c.SourceId WHERE s.DocumentId=$id))
                WHERE Id=$id
                """, ("$id", document.Id));
        tx.Commit();
        return true;
    }

    private void FinishPage(Reconciliation claim, string cursor, bool finished)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (Execute(db, """
            UPDATE SearchCheckpoints SET Cursor=$cursor,Epoch=$epoch,CompletedUtc=CASE WHEN $finished THEN $utc ELSE CompletedUtc END,
                Error=NULL,Attempts=0,RetryAt=0
            WHERE Kind=$kind AND Lease=$lease AND LeaseUntil>$now
            """, ("$cursor", finished ? "" : cursor), ("$epoch", claim.Epoch + (finished ? 1 : 0)),
            ("$finished", finished), ("$utc", DateTime.UtcNow.ToString("O")), ("$kind", claim.Kind), ("$lease", claim.Lease), ("$now", Now)) != 1) return;
        if (finished)
            Execute(db, "DELETE FROM SearchDocuments WHERE Kind=$kind AND SeenEpoch<>$epoch", ("$kind", claim.Kind), ("$epoch", claim.Epoch));
        tx.Commit();
    }

    private (IReadOnlyList<SearchDocument> Documents, string Cursor) ReadHistoryPage(string kind, string cursor, int size, CancellationToken ct)
    {
        // An unavailable source is not an empty corpus. Preserve its previous index and
        // checkpoint until a successful snapshot can establish which records were deleted.
        if (!File.Exists(statePath)) throw new IOException("History source database is unavailable.");
        using var db = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder { DataSource = statePath }.ToString(), readOnly: true);
        using var tx = db.BeginTransaction(deferred: true);
        using var schema = Command(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('Sessions','UserInputs','InputFileChanges')");
        if (Convert.ToInt32(schema.ExecuteScalar()) != 3)
            throw new InvalidOperationException("History source schema is not ready.");
        var documents = new List<SearchDocument>();
        if (kind == "input")
        {
            using var cmd = Command(db, """
                SELECT u.Id,u.SessionId,u.Sequence,u.InputText,u.GitCommitHash,u.TimestampUTC,
                    s.Cli,s.EnvironmentName,s.WorkingDirectory
                FROM UserInputs u LEFT JOIN Sessions s ON s.Id=u.SessionId WHERE u.Id>$after ORDER BY u.Id LIMIT $size
                """, ("$after", long.TryParse(cursor, out var value) ? value : 0), ("$size", size));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.GetInt64(0);
                cursor = id.ToString(CultureInfo.InvariantCulture);
                var session = reader.GetString(1);
                var text = InputEtlFilter.Process(reader.GetString(3));
                var documentId = BertDocumentId.Create(session, id);
                var files = ReadFiles(db, id, null);
                var metadata = new BertInputMetadata(documentId, session, id, reader.GetInt32(2), text ?? "", Nullable(reader, 4),
                    DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Nullable(reader, 6), Nullable(reader, 7), Nullable(reader, 8), files.Count);
                documents.Add(new(documentId, kind, metadata.WorkingDirectory ?? "", text is null ? [] : [new("input", text)],
                    Input: metadata, FileChanges: files));
            }
        }
        else
        {
            using var cmd = Command(db, """
                SELECT Id,Cli,EnvironmentName,WorkingDirectory,StartedUTC,EndedUTC FROM Sessions
                WHERE Id>$after AND EndedUTC IS NOT NULL ORDER BY Id LIMIT $size
                """, ("$after", cursor), ("$size", size));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var session = reader.GetString(0);
                cursor = session;
                using var inputs = Command(db, "SELECT Id,InputText FROM UserInputs WHERE SessionId=$id ORDER BY Sequence,Id", ("$id", session));
                var texts = new List<string>();
                using (var input = inputs.ExecuteReader())
                    while (input.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        var safe = InputEtlFilter.Process(input.GetString(1));
                        if (safe is not null) texts.Add(safe);
                    }
                var documentId = BertSessionDocumentId.Create(session, 0);
                var files = ReadFiles(db, null, session);
                var metadata = new BertSessionMetadata(documentId, session, 0, Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3),
                    DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), texts.Count, 0, files);
                documents.Add(new(documentId, kind, metadata.WorkingDirectory ?? "",
                    texts.Count == 0 ? [] : [new("session", string.Join("\n\n", texts))], Session: metadata, FileChanges: files));
            }
        }
        return (documents, cursor);
    }

    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static IReadOnlyList<BertFileChangeResponse> ReadFiles(SqliteConnection db, long? input, string? session)
    {
        using var cmd = Command(db, """
            SELECT f.FilePath,f.ChangeType,f.LinesAdded,f.LinesDeleted FROM InputFileChanges f
            JOIN UserInputs u ON u.Id=f.UserInputId
            WHERE ($input IS NOT NULL AND u.Id=$input) OR ($session IS NOT NULL AND u.SessionId=$session)
            ORDER BY u.Sequence,f.Id
            """, ("$input", input), ("$session", session));
        var files = new Dictionary<string, BertFileChangeResponse>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            files.TryGetValue(path, out var previous);
            files[path] = new(path, reader.GetString(1), (previous?.LinesAdded ?? 0) + (reader.IsDBNull(2) ? 0 : reader.GetInt32(2)),
                (previous?.LinesDeleted ?? 0) + (reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
        }
        return files.Values.ToArray();
    }
}
