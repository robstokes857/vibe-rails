using System.Security.Cryptography;
using System.Text.Json;

namespace VibeRails.Services.Board.Sync;

/// <summary>
/// Refreshes a bounded, rotating slice of published cards. The singleton activity cache persists
/// across scoped sync services; the database cursor survives restarts. The existing
/// cross-process sync lock serializes calls. No cache entry is an acknowledgement until the server
/// names the schema and card it accepted.
/// </summary>
internal static class BoardSyncActivity
{
    internal const int MaxSnapshotBytes = 8 * 1024 * 1024;
    internal const int MaxAttachmentBytes = 1024 * 1024;
    internal const int CardsPerTick = 10;
    private const int MaxCodeChars = 256 * 1024;
    private const int ContentBudget = 5 * 1024 * 1024;
    internal static async Task<BoardSyncLinkRecord> RefreshAsync(IBoardStore store, IBoardSyncClient client,
        BoardSyncActivityCache cache, BoardSyncLinkRecord link, CancellationToken ct, bool force = false)
    {
        var identity = link.DestinationKey + ":" + link.RemoteBoardId + ":" + link.BoardId;
        var ids = await store.GetSyncActivityCardIdsAsync(link.ProjectPath, link.BoardId, link.ActivityAfter, CardsPerTick, ct);
        if (ids.Count == 0 && link.ActivityAfter is not null)
        {
            link = link with { ActivityAfter = null };
            ids = await store.GetSyncActivityCardIdsAsync(link.ProjectPath, link.BoardId, null, CardsPerTick, ct);
        }
        Exception? firstFailure = null;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var key = identity + ":" + id;
                var previous = cache.Get(key);
                var now = cache.UtcNow;
                // Check before reading activity, state outcomes, file snapshots or attachments.
                // Manual Sync now bypasses this short freshness window.
                if (!force && previous is not null && now - previous.CheckedUtc < BoardSyncActivityCache.RefreshInterval)
                {
                    link = link with { ActivityAfter = id };
                    continue;
                }
                var metadata = await store.GetSyncActivityAsync(link.ProjectPath, link.BoardId, id, ct);
                if (metadata is not null)
                {
                    var activity = await CaptureAsync(store, link.ProjectPath, id, metadata, ct);
                    var json = JsonSerializer.SerializeToUtf8Bytes(activity, BoardSyncJsonContext.Default.BoardSyncActivityWire);
                    var hash = Convert.ToHexString(SHA256.HashData(json));
                    var uploaded = previous?.UploadedUtc;
                    if (previous is null || previous.Hash != hash || previous.UploadedUtc < now.AddHours(-1))
                    {
                        var ack = await client.PutActivityAsync(link.RemoteBoardId, id, activity, ct, link.DestinationKey);
                        if (ack.Schema != 1 || ack.CardId != id)
                            throw new BoardSyncClientException("The server did not acknowledge this card's activity; the upload will retry.", "invalid_response");
                        uploaded = now;
                    }
                    cache.Set(key, new(hash, now, uploaded!.Value));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { firstFailure ??= ex; }
            // A malformed/missing card must not starve the rest. Its failed hash is never
            // acknowledged, so its next turn retries, even after a process restart.
            link = link with { ActivityAfter = id };
        }
        link = await store.SaveSyncLinkAsync(link, ct) ?? link;
        if (firstFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
        return link;
    }

    internal static async Task<BoardSyncActivityWire> CaptureAsync(IBoardStore store, string projectPath,
        string cardId, BoardSyncActivityRecord metadata, CancellationToken ct)
    {
        var warnings = new List<string>();
        void Warn(string message) { if (warnings.Count < 20 && !warnings.Contains(message)) warnings.Add(Clip(message, 500)); }
        var sessions = metadata.Sessions.OrderByDescending(s => s.CreatedUtc).Take(200).ToList();
        var sessionIds = sessions.Select(s => s.SessionId).ToList();
        var automations = await store.GetAutomationSessionIdsAsync(projectPath, sessionIds, ct);
        var outcomes = await store.GetSyncSessionOutcomesAsync(sessionIds, ct);
        var remaining = ContentBudget;
        var sessionViews = new List<BoardSyncSessionWire>();
        foreach (var session in sessions)
        {
            var outcome = outcomes.GetValueOrDefault(session.SessionId);
            var summary = outcome?.Summary;
            if (summary is not null)
            {
                var limit = Math.Min(16000, remaining / 6);
                if (summary.Length > limit)
                    Warn("Some older session summaries were shortened or omitted to fit the hosted snapshot; full summaries remain on the desktop.");
                summary = limit == 0 ? null : Clip(summary, limit);
                remaining -= (summary?.Length ?? 0) * 6;
            }
            sessionViews.Add(new BoardSyncSessionWire(session.SessionId, Clip(session.DisplayName, 500), Clip(session.Cli, 100),
                Clip(session.Origin, 100), session.CreatedUtc,
                session.Origin == BoardSessionRecord.AutomationOrigin || automations.Contains(session.SessionId),
                outcome?.EndedUtc, outcome?.ExitCode, summary));
        }
        var result = new BoardSyncActivityWire(1, sessionViews,
            [], [], metadata.LinkedCards.Take(100).Select(c => new BoardSyncLinkedCardWire(c.Id, c.Key,
                c.DisplayId, Clip(c.Title, 500), c.BoardId)).ToList(), warnings);
        if (metadata.Sessions.Count > 200) Warn("Only the newest 200 linked sessions are included in this hosted snapshot.");
        if (metadata.Commits.Count > 200) Warn("Only the newest 200 linked commits are included in this hosted snapshot.");
        if (metadata.LinkedCards.Count > 100) Warn("Only the first 100 linked cards are included in this hosted snapshot.");
        if (metadata.Attachments.Count > 40) Warn("Only the first 40 attachments are included in this hosted snapshot.");

        // Budget the worst JSON expansion (six UTF-8 bytes per UTF-16 code unit), leaving room
        // for all metadata and warnings. Keep the saved local snapshot untouched.
        foreach (var commit in metadata.Commits.OrderByDescending(c => c.CommittedUtc).Take(200))
        {
            var files = new List<BoardSyncCommitFileWire>();
            var snapshot = remaining > 0 ? await store.GetSyncCommitSnapshotAsync(projectPath, cardId, commit.Sha, MaxSnapshotBytes, ct) : null;
            if (snapshot is null)
                Warn("Some saved commit content is unavailable in this hosted snapshot; the full stored snapshot remains on the desktop.");
            else
            {
                foreach (var file in snapshot.Files.Take(100))
                {
                    var truncated = false;
                    string Content(string value)
                    {
                        var limit = Math.Min(MaxCodeChars, remaining / 6);
                        if (value.Length <= limit) { remaining -= value.Length * 6; return value; }
                        Warn("Some commit files are truncated to fit the hosted snapshot; full saved content remains on the desktop.");
                        truncated = true;
                        const string marker = "\n… (truncated for hosted snapshot)";
                        if (limit < marker.Length) return "(omitted from hosted snapshot)";
                        var bounded = value[..(limit - marker.Length)] + marker;
                        remaining -= bounded.Length * 6;
                        return bounded;
                    }
                    var before = Content(file.OriginalContent);
                    var after = Content(file.ModifiedContent);
                    files.Add(new(Clip(file.FileName, 2048), truncated ? "truncated" : "changed", before, after));
                }
                if (snapshot.Files.Count > 100) Warn("Some commits have more than 100 files; remaining files are available on the desktop.");
            }
            result.Commits.Add(new(commit.Sha, Clip(commit.Author, 500), Clip(commit.Message, 16000), commit.CommittedUtc, files));
        }
        foreach (var attachment in metadata.Attachments.Take(40))
        {
            string? content = null;
            string? reason = null;
            // Metadata is read without the BLOB/data URL, so an unlimited local upload does not
            // become an unlimited sync read. Content is checked again before serialization.
            if (attachment.Bytes > MaxAttachmentBytes)
                reason = "This file exceeds the 1 MiB hosted attachment limit. Open it in the desktop board.";
            else if (attachment.Bytes < 0 || ((attachment.Bytes + 2) / 3 * 4) > remaining)
                reason = "This file's content does not fit this hosted snapshot. Open it in the desktop board.";
            else
            {
                var loaded = await store.GetSyncAttachmentContentAsync(projectPath, cardId, attachment.Id, MaxAttachmentBytes, ct);
                if (loaded is null || loaded.LongLength != attachment.Bytes || loaded.Length > MaxAttachmentBytes)
                    reason = "This file's content is unavailable. Open it in the desktop board.";
                else
                {
                    content = Convert.ToBase64String(loaded);
                    remaining -= content.Length;
                }
            }
            result.Attachments.Add(new(attachment.Id, Clip(attachment.Name, 255), Clip(attachment.MimeType, 100),
                Math.Max(0, attachment.Bytes), attachment.CreatedUtc, content, reason));
        }
        // Unusually verbose metadata can itself consume the budget. Drop content first, then
        // oldest summaries/commit metadata, with a visible explanation. Measure the whole payload
        // once, then subtract each changed item's encoded size instead of serializing it N times.
        var size = JsonSerializer.SerializeToUtf8Bytes(result, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length;
        const int targetSize = MaxSnapshotBytes - 1024 * 1024;
        if (size > targetSize)
        {
            const string warning = "This activity snapshot was shortened to fit the hosted transfer limit. Full activity remains on the desktop.";
            Warn(warning);
            size += warning.Length * 6 + 3;
        }
        while (size > targetSize)
        {
            var attachmentIndex = result.Attachments.FindLastIndex(a => a.ContentBase64 is not null);
            if (attachmentIndex >= 0)
            {
                var before = JsonSerializer.SerializeToUtf8Bytes(result.Attachments[attachmentIndex], BoardSyncJsonContext.Default.BoardSyncAttachmentWire).Length;
                result.Attachments[attachmentIndex] = result.Attachments[attachmentIndex] with
                { ContentBase64 = null, UnavailableReason = "Content exceeds the hosted snapshot transfer limit." };
                size -= before - JsonSerializer.SerializeToUtf8Bytes(result.Attachments[attachmentIndex], BoardSyncJsonContext.Default.BoardSyncAttachmentWire).Length;
            }
            else if (result.Sessions.FindLastIndex(s => s.Summary is not null) is var summaryIndex && summaryIndex >= 0)
            {
                var before = JsonSerializer.SerializeToUtf8Bytes(result.Sessions[summaryIndex], BoardSyncJsonContext.Default.BoardSyncSessionWire).Length;
                result.Sessions[summaryIndex] = result.Sessions[summaryIndex] with { Summary = null };
                size -= before - JsonSerializer.SerializeToUtf8Bytes(result.Sessions[summaryIndex], BoardSyncJsonContext.Default.BoardSyncSessionWire).Length;
            }
            else if (result.Commits.Count > 0)
            {
                size -= JsonSerializer.SerializeToUtf8Bytes(result.Commits[^1], BoardSyncJsonContext.Default.BoardSyncCommitWire).Length;
                result.Commits.RemoveAt(result.Commits.Count - 1);
            }
            else throw new BoardValidationException("The card activity metadata exceeds the hosted transfer limit.");
        }
        return result;
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
}
