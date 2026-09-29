using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VibeRails.Services.Diagnostics;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

/// <summary>What the board editor shows: whether the board is published, where, and how the last sync went.</summary>
public sealed record BoardSyncStatus(
    string BoardId,
    bool Published,
    bool Enabled,
    string? RemoteBoardId,
    string? RemoteUrl,
    long Cursor,
    int Unsent,
    DateTime? LastSyncUtc,
    string? LastError,
    bool Configured,
    int Rejected = 0,
    IReadOnlyList<BoardSyncRejectedEntry>? RejectedEntries = null,
    int Skipped = 0,
    IReadOnlyList<BoardSyncSkippedEntry>? SkippedEntries = null);

public interface IBoardSyncService
{
    /// <summary>Null when the board does not exist in the project.</summary>
    Task<BoardSyncStatus?> GetStatusAsync(string projectPath, string boardId, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the board (creating or refreshing its copy on viberails.ai, writing the baseline
    /// and running a first sync) or switches its sync off. Switching off keeps the link and the
    /// cursor, so switching back on resumes where it stopped.
    /// </summary>
    Task<BoardSyncStatus?> SetPublishedAsync(string projectPath, string boardId, bool enabled, CancellationToken cancellationToken);

    /// <summary>One push-then-pull for this board now; errors land in the status, not the caller.</summary>
    Task<BoardSyncStatus?> SyncNowAsync(string projectPath, string boardId, CancellationToken cancellationToken);

    /// <summary>Every enabled board, for the root scheduler. One board's failure never stops the next.</summary>
    Task SyncDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Push-then-pull of Card Log entries between a local board and its copy on viberails.ai (VB-51).
/// Push sends every unsent entry oldest first and records the server sequence each received. Pull
/// reads the server's entries after the cursor and applies the ones this machine has never seen
/// through the store's stamped writes, so a web change becomes a normal local change (lane
/// Automations included) whose log row already counts as sent. Two sides editing the same field
/// are resolved on the server by arrival order; locally a field with an unsent edit is left alone,
/// because that edit reaches the server after the web one and wins there.
/// </summary>
public sealed class BoardSyncService(
    IBoardStore store,
    IBoardSyncClient client,
    BoardSyncLock syncLock,
    IFeatureLog featureLog) : IBoardSyncService
{
    public const string FeatureName = "board-sync";

    // Bounds one tick: 25 × 20 entries each way. A board further behind catches up over ticks.
    private const int MaxPushBatchesPerSync = 25;
    private const int PushBatchSize = 20;
    private const int PullPageSize = 20;
    private const int MaxPullPagesPerSync = 25;
    private const int MaxErrorLength = 500;

    public async Task<BoardSyncStatus?> GetStatusAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        var board = await store.GetBoardAsync(projectPath, boardId, cancellationToken);
        if (board is null)
            return null;
        return await StatusAsync(board.Id, await store.GetSyncLinkAsync(projectPath, boardId, cancellationToken), cancellationToken);
    }

    public async Task<BoardSyncStatus?> SetPublishedAsync(string projectPath, string boardId, bool enabled, CancellationToken cancellationToken)
    {
        using var held = syncLock.TryAcquire()
            ?? throw new BoardValidationException("A Board sync is already running. Try again when it finishes.");
        var board = await store.GetBoardAsync(projectPath, boardId, cancellationToken);
        if (board is null)
            return null;
        var existing = await store.GetSyncLinkAsync(projectPath, boardId, cancellationToken);

        if (!enabled)
        {
            if (existing is not null && existing.Enabled)
            {
                existing = await store.SaveSyncLinkAsync(existing with { Enabled = false }, cancellationToken) ?? existing;
                featureLog.Write(FeatureName, "unpublish", $"Sync switched off for board \"{board.Name}\".", board.Id, existing.RemoteBoardId, "ok");
            }
            return await StatusAsync(board.Id, existing, cancellationToken);
        }

        if (!client.IsConfigured)
            throw new BoardValidationException("Add your viberails.ai API key in Settings before publishing a board.");

        var layout = await ReadLayoutAsync(projectPath, board, cancellationToken);
        var destination = client.DestinationKey ?? throw new BoardValidationException("Board sync is not configured.");
        BoardSyncPublishResponse published;
        try
        {
            published = await client.PublishAsync(new BoardSyncPublishRequest(board.Id, board.Name, layout.Prefix, layout.Lanes, board.EffectiveDisplayPrefix), cancellationToken, destination);
        }
        catch (BoardSyncClientException ex)
        {
            featureLog.Write(FeatureName, "publish", Trim(ex.Message), board.Id, existing?.RemoteBoardId, "failed", LogLevel.Warning);
            throw new BoardValidationException("Publishing failed: " + ex.Message);
        }

        var remoteBoardId = published.RemoteBoardId.ToString("D");
        // The same local board published under another account's key is a different remote board
        // that has none of this history: start its ledger and cursor over.
        var remoteChanged = existing is not null && !string.Equals(existing.RemoteBoardId, remoteBoardId, StringComparison.OrdinalIgnoreCase);
        if (remoteChanged)
            await store.ResetSentMarksAsync(board.Id, cancellationToken);
        var link = await store.SaveSyncLinkAsync(new BoardSyncLinkRecord(
            board.Id,
            remoteBoardId,
            Cursor: remoteChanged ? 0 : existing?.Cursor ?? 0,
            Enabled: true,
            LayoutHash: layout.Hash,
            LastSyncUtc: remoteChanged ? null : existing?.LastSyncUtc,
            LastError: null,
            CreatedUtc: existing?.CreatedUtc ?? default,
            UpdatedUtc: DateTime.UtcNow,
            projectPath,
            board.Name, destination), cancellationToken)
            ?? throw new BoardValidationException("That board no longer exists. Open the board again and retry.");

        var baseline = await store.WriteSyncBaselineAsync(projectPath, board.Id, cancellationToken);
        featureLog.Write(FeatureName, "publish", $"Published board \"{board.Name}\"; {baseline} card(s) given a baseline entry.", board.Id, remoteBoardId, "ok");
        link = await SyncLinkAsync(link, cancellationToken);
        return await StatusAsync(board.Id, link, cancellationToken);
    }

    public async Task<BoardSyncStatus?> SyncNowAsync(string projectPath, string boardId, CancellationToken cancellationToken)
    {
        using var held = syncLock.TryAcquire()
            ?? throw new BoardValidationException("A Board sync is already running. Try again when it finishes.");
        var board = await store.GetBoardAsync(projectPath, boardId, cancellationToken);
        if (board is null)
            return null;
        var link = await store.GetSyncLinkAsync(projectPath, boardId, cancellationToken);
        if (link is { Enabled: true })
            link = await SyncLinkAsync(link, cancellationToken);
        return await StatusAsync(board.Id, link, cancellationToken);
    }

    public async Task SyncDueAsync(CancellationToken cancellationToken)
    {
        // Without a key nothing can leave the machine; the status view says so per board.
        if (!client.IsConfigured)
            return;
        // A root backend that lost the scheduler lease mid-sync may still be syncing; skip rather
        // than run the same board twice. The next interval tries again.
        using var held = syncLock.TryAcquire();
        if (held is null)
            return;
        foreach (var link in await store.GetSyncLinksAsync(cancellationToken))
        {
            if (!link.Enabled)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            await SyncLinkAsync(link, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ one board

    /// <summary>Push then pull. Every failure is recorded on the link and never thrown, except cancellation.</summary>
    private async Task<BoardSyncLinkRecord> SyncLinkAsync(BoardSyncLinkRecord link, CancellationToken cancellationToken)
    {
        try
        {
            if (link.DestinationKey is null || link.DestinationKey != client.DestinationKey)
                throw new BoardValidationException("The server or API key changed. Turn publishing off and on to approve this destination.");
            link = await PushAsync(link, cancellationToken);
            link = await PullAsync(link, cancellationToken);
            return await store.SaveSyncLinkAsync(link with { LastSyncUtc = DateTime.UtcNow, LastError = null }, cancellationToken) ?? link;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is BoardSyncClientException or BoardValidationException
                ? ex.Message
                : "Sync failed (" + ex.GetType().Name + "). Retry or check the connection.";
            featureLog.Write(FeatureName, "sync", Trim(message), link.BoardId, link.RemoteBoardId, "failed", LogLevel.Warning);
            // Push and pull persist progress as they go (the layout hash after a batch, the cursor
            // after each page). Record the error on the stored link rather than on the stale copy
            // this method holds, so a failure on page three never rewinds the cursor past pages one
            // and two and forces every later tick to re-fetch them.
            var current = await store.GetSyncLinkAsync(link.ProjectPath, link.BoardId, CancellationToken.None) ?? link;
            var failed = current with { LastError = Trim(message) };
            return await store.SaveSyncLinkAsync(failed, CancellationToken.None) ?? failed;
        }
    }

    private async Task<BoardSyncLinkRecord> PushAsync(BoardSyncLinkRecord link, CancellationToken cancellationToken)
    {
        var board = await store.GetBoardAsync(link.ProjectPath, link.BoardId, cancellationToken)
            ?? throw new BoardValidationException("The board no longer exists.");
        // A card an older binary created since publishing has no created entry, so without a baseline
        // nothing about it (its comments included) would ever leave the queue.
        var baseline = await store.WriteSyncBaselineAsync(link.ProjectPath, link.BoardId, cancellationToken);
        if (baseline > 0)
            featureLog.Write(FeatureName, "baseline", $"{baseline} card(s) without a created entry given a baseline entry.", link.BoardId, link.RemoteBoardId, "ok");
        var layout = await ReadLayoutAsync(link.ProjectPath, board, cancellationToken);
        var lastKnownSequence = Math.Max(link.Cursor, await store.GetMaxAcknowledgedSequenceAsync(link.BoardId, cancellationToken));
        var pushed = 0;
        for (var batch = 0; batch < MaxPushBatchesPerSync; batch++)
        {
            var unsent = await store.GetUnsentLogEntriesAsync(link.BoardId, PushBatchSize, cancellationToken);
            var sendLayout = !string.Equals(layout.Hash, link.LayoutHash, StringComparison.Ordinal);
            if (unsent.Count == 0 && !sendLayout)
                break;

            var request = new BoardSyncPushRequest(
                sendLayout ? board.Name : null,
                sendLayout ? layout.Prefix : null,
                sendLayout ? layout.Lanes : null,
                unsent.Select(ToWire).ToList(), sendLayout ? board.EffectiveDisplayPrefix : null);
            BoardSyncPushResponse response;
            try
            {
                response = await client.PushAsync(link.RemoteBoardId, request, cancellationToken, link.DestinationKey);
            }
            catch (BoardSyncClientException ex) when (ex.Code == BoardSyncWire.CodeInvalidEntry
                && ex.EntryId is { } rejectedId && unsent.Any(e => e.Entry.Id == rejectedId))
            {
                if (!await store.RejectLogEntryAsync(link.BoardId, ex.EntryId!, cancellationToken)) throw;
                featureLog.Write(FeatureName, "reject", "A rejected entry was retained locally; later entries can continue syncing.",
                    link.BoardId, link.RemoteBoardId, "rejected", LogLevel.Warning);
                continue;
            }
            ValidateAcknowledgement(response, unsent, lastKnownSequence);

            await store.MarkLogEntriesSentAsync(
                response.Accepted.Select(a => new KeyValuePair<string, long>(a.Id, a.Seq)).ToList(), cancellationToken);
            lastKnownSequence = response.LastSeq;
            pushed += response.Accepted.Count;
            if (sendLayout)
                link = await store.SaveSyncLinkAsync(link with { LayoutHash = layout.Hash }, cancellationToken) ?? link with { LayoutHash = layout.Hash };
            if (unsent.Count < PushBatchSize)
                break;
        }
        if (pushed > 0)
            featureLog.Write(FeatureName, "push", $"Pushed {pushed} Card Log entr{(pushed == 1 ? "y" : "ies")}.", link.BoardId, link.RemoteBoardId, "ok");
        return link;
    }

    private static void ValidateAcknowledgement(BoardSyncPushResponse response, IReadOnlyList<BoardSyncOutboundEntry> sent, long cursor)
    {
        const string message = "The server returned an invalid sync acknowledgement; entries remain queued.";
        if (response.Accepted is null || response.Accepted.Count != sent.Count || response.LastSeq < cursor || response.LastSeq < 0)
            throw new BoardValidationException(message);
        var sequences = new HashSet<long>();
        long lastCreated = 0, lastOther = 0;
        for (var index = 0; index < sent.Count; index++)
        {
            var accepted = response.Accepted[index];
            if (accepted is null || accepted.Id != sent[index].Entry.Id || accepted.Seq <= 0
                || accepted.Seq > response.LastSeq || !sequences.Add(accepted.Seq))
                throw new BoardValidationException(message);
            // The outbox orders each group by insertion order. A new creation can move ahead
            // of a retried change whose ACK was lost; those groups may legitimately interleave
            // server sequences. Each group's own order must remain monotonic, including retries.
            if (sent[index].Entry.Kind == BoardSyncWire.KindCreated)
            {
                if (accepted.Seq <= lastCreated) throw new BoardValidationException(message);
                lastCreated = accepted.Seq;
            }
            else
            {
                if (accepted.Seq <= lastOther) throw new BoardValidationException(message);
                lastOther = accepted.Seq;
            }
        }
    }

    private async Task<BoardSyncLinkRecord> PullAsync(BoardSyncLinkRecord link, CancellationToken cancellationToken)
    {
        var applied = 0;
        var skipped = 0;
        for (var page = 0; page < MaxPullPagesPerSync; page++)
        {
            var response = await client.PullAsync(link.RemoteBoardId, link.Cursor, PullPageSize, cancellationToken, link.DestinationKey);
            ValidatePullPage(response, link.Cursor);
            // Lanes are read per page: a pulled entry may have just moved or created cards.
            var columns = await store.GetColumnsAsync(link.ProjectPath, cancellationToken, link.BoardId);
            foreach (var entry in response.Entries)
            {
                // Own pushed entries come back too; they, and anything applied by an earlier
                // interrupted pass, already exist under their id.
                if (!await store.HasLogEntryAsync(entry.Id, cancellationToken))
                {
                    try
                    {
                        if (await ApplyAsync(link, columns, entry, cancellationToken))
                            applied++;
                    }
                    catch (BoardValidationException ex)
                    {
                        // Permanent: no retry makes this version able to apply the entry, and waiting
                        // on it would hold back everything after it. It is recorded, logged and counted
                        // in the status view. Transient failures (a busy database) still throw and retry.
                        await store.RecordSkippedSyncEntryAsync(link.BoardId,
                            new BoardSyncSkippedEntry(entry.Id, entry.Seq, entry.CardKey, SkippedKind(entry.Kind), Trim(ex.Message)), cancellationToken);
                        featureLog.Write(FeatureName, "skip", $"Skipped remote entry {entry.Id} ({SkippedKind(entry.Kind)} on {entry.CardKey}): {Trim(ex.Message)}",
                            link.BoardId, link.RemoteBoardId, "skipped", LogLevel.Warning);
                        skipped++;
                    }
                }
                link = link with { Cursor = entry.Seq };
            }
            link = await store.SaveSyncLinkAsync(link, cancellationToken) ?? link;
            if (!response.HasMore)
                break;
        }
        if (applied > 0)
            featureLog.Write(FeatureName, "pull", $"Applied {applied} web entr{(applied == 1 ? "y" : "ies")}.", link.BoardId, link.RemoteBoardId, "ok");
        if (skipped > 0)
            featureLog.Write(FeatureName, "pull", $"Skipped {skipped} web entr{(skipped == 1 ? "y" : "ies")} this version cannot apply.", link.BoardId, link.RemoteBoardId, "skipped", LogLevel.Warning);
        return link;
    }

    private static void ValidatePullPage(BoardSyncPullResponse response, long cursor)
    {
        const string message = "The server returned an invalid sync page.";
        if (response.Entries is null || response.Entries.Count > PullPageSize || response.LastSeq < cursor
            || (response.HasMore && response.Entries.Count == 0))
            throw new BoardValidationException(message);
        // Check the entire page before applying any of it, including entries already held locally.
        // Sequences only rise. A gap (a sequence the server no longer serves) is passed over: requiring
        // cursor + 1 would stop every later tick on it for good.
        foreach (var entry in response.Entries)
        {
            if (entry is null || entry.Seq <= cursor || entry.Seq > response.LastSeq)
                throw new BoardValidationException(message);
            ValidatePulledEntryShape(entry);
            cursor = entry.Seq;
        }
        // More can follow only a page that stopped short of the advertised end; a final page may stop
        // short of it too, when the newest sequences are gaps.
        if (response.HasMore && cursor >= response.LastSeq)
            throw new BoardValidationException(message);
    }

    /// <summary>A bounded label for an entry kind this version may not know, for the skipped-entry record.</summary>
    private static string SkippedKind(string? kind) =>
        IsOpaqueId(kind) && kind!.Length <= 32 ? kind : "unknown";

    /// <summary>
    /// Applies and retains one unseen entry. A <see cref="BoardValidationException"/> means this
    /// version can never apply it (unsupported kind or value, conflicting identity, a card of another
    /// board or one that never reached this machine): the pull records and passes it. Losing field
    /// edits are retained by the store without overwriting later local changes.
    /// </summary>
    private async Task<bool> ApplyAsync(BoardSyncLinkRecord link, IReadOnlyList<BoardColumnRecord> columns, BoardSyncPulledEntryWire entry, CancellationToken cancellationToken)
    {
        ValidatePulledEntryContent(entry);
        var author = ToAuthor(entry.Author);
        var stamp = new BoardSyncStamp(entry.Id, entry.Seq, entry.CreatedUtc, entry.Body, entry.Changes?.GetRawText(), link.BoardId);
        bool applied;
            switch (entry.Kind)
            {
                case BoardSyncWire.KindCreated:
                    applied = await store.CreateSyncedCardAsync(link.ProjectPath, entry.CardId, entry.CardKey,
                        NewCardFrom(entry.Changes, columns, link.BoardId), author, stamp, cancellationToken) is not null;
                    break;

                case BoardSyncWire.KindChange:
                {
                    // The store checks for later local edits inside the same transaction as the
                    // write. The complete remote log entry survives even when no field changes.
                    var patch = PatchFrom(entry.Changes, columns);
                    applied = await store.UpdateSyncedCardAsync(link.ProjectPath, entry.CardId, patch, author, stamp, cancellationToken) is not null;
                    break;
                }

                case BoardSyncWire.KindDeleted:
                {
                    applied = await store.DeleteSyncedCardAsync(link.ProjectPath, entry.CardId, author, stamp, cancellationToken);
                    break;
                }

                case BoardSyncWire.KindComment:
                case BoardSyncWire.KindNote:
                {
                    if (entry.Body.Length > BoardService.MaxCommentLength)
                        throw new BoardValidationException("The remote comment exceeds the local size limit.");
                    applied = await store.AddSyncedCommentAsync(link.ProjectPath, entry.CardId, author, entry.Body, entry.Kind, stamp, cancellationToken) is not null;
                    break;
                }

                default:
                    throw new BoardValidationException("This version cannot apply remote entry kind: " + SkippedKind(entry.Kind));
            }
        // Entries apply in server order, so a card's creation came first: a card still missing now
        // was never created here (its creation was skipped or belongs to another project).
        if (!applied)
            throw new BoardValidationException("The remote entry refers to a card this machine does not have.");
        return true;
    }

    // ------------------------------------------------------------------ mapping

    private async Task<(string Prefix, List<BoardSyncLaneWire> Lanes, string Hash)> ReadLayoutAsync(string projectPath, BoardRecord board, CancellationToken cancellationToken)
    {
        var prefix = await store.EnsureProjectKeyPrefixAsync(projectPath, cancellationToken);
        var lanes = (await store.GetColumnsAsync(projectPath, cancellationToken, board.Id))
            .OrderBy(c => c.Position)
            .Select((c, index) => new BoardSyncLaneWire(c.Id, c.Name, string.IsNullOrWhiteSpace(c.Color) ? null : c.Color, index))
            .ToList();
        var canonical = new StringBuilder().Append(board.Name).Append('\n').Append(prefix).Append('\n').Append(board.EffectiveDisplayPrefix);
        foreach (var lane in lanes)
            canonical.Append('\n').Append(lane.Id).Append('\t').Append(lane.Name).Append('\t').Append(lane.Color).Append('\t').Append(lane.Position);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return (prefix, lanes, hash);
    }

    internal static BoardSyncEntryWire ToWire(BoardSyncOutboundEntry outbound)
    {
        var entry = outbound.Entry;
        var changes = PortableChanges(entry.Changes);
        // Generated summaries may contain machine-local environment ids. The portable fields
        // provide the full values; comments/notes remain the user's verbatim text.
        var body = entry.Kind is BoardSyncWire.KindCreated or BoardSyncWire.KindChange
            ? entry.Kind == BoardSyncWire.KindCreated ? "Created" : "Changed: " + string.Join(", ", changes?.EnumerateObject().Select(p => p.Name) ?? [])
            : entry.Body;
        return new BoardSyncEntryWire(
            entry.Id,
            entry.CardId,
            outbound.CardKey,
            entry.Kind,
            new BoardSyncAuthorWire(entry.Author.Kind, entry.Author.Label, entry.Author.Cli),
            body,
            entry.CreatedUtc,
            changes);
    }

    private static JsonElement? PortableChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var source = JsonNode.Parse(json)?.AsObject() ?? throw new BoardValidationException("A local change entry is invalid.");
        var portable = new JsonObject();
        // "context" is not a card field: it is the agent-context sample a launch records (VB-63,
        // BoardStore.ContextSamples.cs). The hosted contract stores unknown change fields verbatim
        // and never applies them, so the numbers reach viberails.ai without a server change.
        foreach (var field in new[] { "displayId", "title", "description", "type", "priority", "points", "assignee", "tags", "blocked", "flagged", "lane", BoardStore.ContextChangeField })
        {
            if (source[field] is not JsonObject values) continue;
            var copy = new JsonObject();
            foreach (var name in new[] { "from", "to" })
            {
                if (!values.ContainsKey(name)) continue;
                var value = values[name];
                if (field == "assignee" && value is not null)
                {
                    var text = value.GetValue<string>();
                    copy[name] = BoardSelection.TryParse(text, out var selection) ? "base:" + selection!.Cli : null;
                }
                else copy[name] = value?.DeepClone();
            }
            portable[field] = copy;
        }
        using var document = JsonDocument.Parse(portable.ToJsonString());
        return document.RootElement.Clone();
    }

    private static BoardAuthor ToAuthor(BoardSyncAuthorWire author)
    {
        var kind = author.Kind is BoardAuthor.UserKind or BoardAuthor.AgentKind or BoardAuthor.SystemKind ? author.Kind : BoardAuthor.UserKind;
        var label = string.IsNullOrWhiteSpace(author.Label) ? (kind == BoardAuthor.UserKind ? "Web user" : "viberails.ai") : author.Label.Trim();
        return new BoardAuthor(kind, label, string.IsNullOrWhiteSpace(author.Cli) ? null : author.Cli, null);
    }

    /// <summary>A web-created card from its <c>created</c> entry. A lane this board no longer has falls back to its first lane.</summary>
    private static NewBoardCard NewCardFrom(JsonElement? changes, IReadOnlyList<BoardColumnRecord> columns, string boardId)
    {
        var laneId = BoardSyncWire.FieldTo(changes, BoardSyncWire.FieldLane) is { ValueKind: JsonValueKind.String } lane ? lane.GetString() : null;
        var column = columns.FirstOrDefault(c => string.Equals(c.Id, laneId, StringComparison.Ordinal))
            ?? columns.OrderBy(c => c.Position).FirstOrDefault()
            ?? throw new BoardValidationException("The board has no lanes.");
        var title = TryString(changes, BoardSyncWire.FieldTitle, BoardService.NormalizeTitle)
            ?? throw new BoardValidationException("A remote creation is missing its title.");
        var description = TryString(changes, BoardSyncWire.FieldDescription, BoardService.NormalizeDescription) ?? string.Empty;
        var type = TryString(changes, BoardSyncWire.FieldType, BoardService.NormalizeCardType) ?? BoardCardTypes.Default;
        var priority = TryString(changes, BoardSyncWire.FieldPriority, BoardService.NormalizePriority) ?? "medium";
        var points = BoardSyncWire.FieldTo(changes, BoardSyncWire.FieldPoints) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var value) ? value : (int?)null;
        var assignee = TryString(changes, BoardSyncWire.FieldAssignee, BoardService.NormalizeAssignee);
        var tags = TagsFrom(changes) ?? [];
        var blocked = BoardSyncWire.FieldTo(changes, BoardSyncWire.FieldBlocked) is { ValueKind: JsonValueKind.True };
        var flagged = BoardSyncWire.FieldTo(changes, BoardSyncWire.FieldFlagged) is { ValueKind: JsonValueKind.True };
        return new NewBoardCard(column.Id, title, description, assignee, priority, points, tags, blocked, null, type, boardId, flagged, DisplayId: TryString(changes, "displayId", value => BoardDisplayIds.Normalize(value!)));
    }

    /// <summary>
    /// The fields of a <c>change</c> entry as a patch. Fields a later local edit or a retained
    /// rejection owns are removed by the store inside the write transaction, not here.
    /// </summary>
    private static BoardCardPatch PatchFrom(JsonElement? changes, IReadOnlyList<BoardColumnRecord> columns)
    {
        var patch = new BoardCardPatch();
        if (changes is not { ValueKind: JsonValueKind.Object } root)
            return patch;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("to", out var to))
                continue;
            switch (property.Name)
            {
                case "displayId":
                    patch = patch with { DisplayId = TryNormalize(to, value => BoardDisplayIds.Normalize(value!)) };
                    break;
                case BoardSyncWire.FieldTitle:
                    patch = patch with { Title = TryNormalize(to, BoardService.NormalizeTitle) };
                    break;
                case BoardSyncWire.FieldDescription:
                    patch = patch with { Description = TryNormalize(to, BoardService.NormalizeDescription) };
                    break;
                case BoardSyncWire.FieldType:
                    patch = patch with { Type = TryNormalize(to, BoardService.NormalizeCardType) };
                    break;
                case BoardSyncWire.FieldPriority:
                    patch = patch with { Priority = TryNormalize(to, BoardService.NormalizePriority) };
                    break;
                case BoardSyncWire.FieldPoints:
                    if (to.ValueKind == JsonValueKind.Null)
                        patch = patch with { ClearPoints = true };
                    else if (to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var points))
                        patch = patch with { Points = points };
                    break;
                case BoardSyncWire.FieldAssignee:
                    if (to.ValueKind == JsonValueKind.Null)
                        patch = patch with { ClearAssignee = true };
                    else
                        patch = patch with { Assignee = TryNormalize(to, BoardService.NormalizeAssignee) };
                    break;
                case BoardSyncWire.FieldTags:
                    patch = patch with { Tags = TagsFrom(changes) };
                    break;
                case BoardSyncWire.FieldBlocked:
                    if (to.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        patch = patch with { Blocked = to.ValueKind == JsonValueKind.True };
                    break;
                case BoardSyncWire.FieldFlagged:
                    if (to.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        patch = patch with { Flagged = to.ValueKind == JsonValueKind.True };
                    break;
                case BoardSyncWire.FieldLane:
                    // Only a lane of this board: a lane deleted since is dropped, never invented.
                    if (to.ValueKind == JsonValueKind.String && columns.Any(c => string.Equals(c.Id, to.GetString(), StringComparison.Ordinal)))
                        patch = patch with { ColumnId = to.GetString() };
                    break;
            }
        }
        return patch;
    }

    private static IReadOnlyList<string>? TagsFrom(JsonElement? changes)
    {
        if (BoardSyncWire.FieldTo(changes, BoardSyncWire.FieldTags) is not { ValueKind: JsonValueKind.Array } array)
            return null;
        var tags = new List<string>();
        foreach (var tag in array.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String)
                tags.Add(tag.GetString()!);
        }
        return BoardService.NormalizeTags(tags);
    }

    private static string? TryString(JsonElement? changes, string field, Func<string?, string?> normalize) =>
        BoardSyncWire.FieldTo(changes, field) is { } to ? TryNormalize(to, normalize) : null;

    /// <summary>Content validation has already refused invalid values; the pull counts those entries as skipped, never silently.</summary>
    private static string? TryNormalize(JsonElement to, Func<string?, string?> normalize)
    {
        if (to.ValueKind != JsonValueKind.String)
            return null;
        return normalize(to.GetString());
    }

    /// <summary>
    /// The page-level checks: identity, author and size bounds. A failure here is a malformed server
    /// response, so the whole page is refused and retried rather than any of it applied.
    /// </summary>
    private static void ValidatePulledEntryShape(BoardSyncPulledEntryWire entry)
    {
        if (!IsOpaqueId(entry.Id) || entry.CardId is null
            || entry.CardId.Length != 17 || !entry.CardId.StartsWith("card_", StringComparison.Ordinal)
            || entry.CardId.Skip(5).Any(c => !"0123456789abcdef".Contains(c))
            || !IsWireCardKey(entry.CardKey)
            || entry.Author is null
            || entry.Author.Kind is not (BoardAuthor.UserKind or BoardAuthor.AgentKind or BoardAuthor.SystemKind)
            || entry.Author.Label is { Length: > BoardSyncWire.MaxAuthorLabelLength }
            || entry.Author.Cli is { Length: > BoardSyncWire.MaxAuthorCliLength }
            || entry.Body is null || entry.Body.Length > (entry.Kind is BoardSyncWire.KindComment or BoardSyncWire.KindNote
                ? BoardService.MaxCommentLength : BoardSyncWire.MaxOtherBodyLength))
            throw new BoardValidationException("The server returned an invalid entry identity or body.");
        if (entry.Changes is { } raw && Encoding.UTF8.GetByteCount(raw.GetRawText()) > BoardSyncWire.MaxChangesBytes)
            throw new BoardValidationException("The remote field changes exceed the sync size limit.");
    }

    /// <summary>
    /// What this version can apply: a known kind, and field values it accepts. A failure is permanent
    /// for this version (a kind or value added after it), so the pull records the entry and passes it.
    /// </summary>
    private static void ValidatePulledEntryContent(BoardSyncPulledEntryWire entry)
    {
        if (entry.Kind is not (BoardSyncWire.KindCreated or BoardSyncWire.KindChange or BoardSyncWire.KindDeleted
            or BoardSyncWire.KindComment or BoardSyncWire.KindNote))
            throw new BoardValidationException("This version cannot apply remote entry kind: " + SkippedKind(entry.Kind));
        if (entry.Kind is not (BoardSyncWire.KindCreated or BoardSyncWire.KindChange)) return;
        if (entry.Changes is not { ValueKind: JsonValueKind.Object } changes)
            throw new BoardValidationException("A remote field change is missing its values.");
        if (entry.Kind == BoardSyncWire.KindCreated
            && (!changes.TryGetProperty(BoardSyncWire.FieldTitle, out _) || !changes.TryGetProperty(BoardSyncWire.FieldLane, out _)))
            throw new BoardValidationException("A remote creation requires title and lane changes.");
        foreach (var field in changes.EnumerateObject())
        {
            // Like the hosted contract, retain unknown fields verbatim without applying them.
            if (field.Name is not ("displayId" or "title" or "description" or "type" or "priority" or "points"
                or "assignee" or "tags" or "blocked" or "flagged" or "lane")) continue;
            if (field.Value.ValueKind != JsonValueKind.Object || !field.Value.TryGetProperty("to", out var to))
                throw new BoardValidationException("A remote field change is missing its new value.");
            var valid = field.Name switch
            {
                "displayId" => to.ValueKind == JsonValueKind.String && IsWireCardKey(to.GetString()),
                "title" => to.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(to.GetString()) && to.GetString()!.Length <= BoardService.MaxTitleLength,
                "description" => to.ValueKind == JsonValueKind.String && to.GetString()!.Length <= BoardService.MaxDescriptionLength,
                "type" => to.ValueKind == JsonValueKind.String && BoardCardTypes.IsValid(to.GetString()),
                "priority" => to.ValueKind == JsonValueKind.String && BoardPriorities.IsValid(to.GetString()),
                "points" => to.ValueKind == JsonValueKind.Null || (to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var p) && p is 1 or 2 or 3 or 5 or 8 or 13),
                "assignee" => to.ValueKind == JsonValueKind.Null || (to.ValueKind == JsonValueKind.String
                    && BoardSelection.TryParse(to.GetString(), out var selected) && !selected!.IsEnvironment && selected.Key == to.GetString()),
                "tags" => to.ValueKind == JsonValueKind.Array && to.GetArrayLength() <= BoardService.MaxTags && to.EnumerateArray().All(t => t.ValueKind == JsonValueKind.String && t.GetString()!.Length <= BoardService.MaxTagLength),
                "blocked" or "flagged" => to.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "lane" => to.ValueKind == JsonValueKind.String && IsOpaqueId(to.GetString()),
                _ => false
            };
            if (!valid) throw new BoardValidationException("The server returned an invalid value for " + field.Name + ".");
        }
    }

    private static bool IsOpaqueId(string? value) => BoardSyncWire.IsOpaqueId(value);

    private static bool IsWireCardKey(string? value)
    {
        if (value is null || value != value.Trim().ToUpperInvariant()
            || (!BoardKeys.TryParseStored(value, out _) && !BoardKeys.TryParse(value, out _, out _))) return false;
        // Input lookup accepts leading zeroes, but the hosted wire requires the stored form.
        return value[value.LastIndexOf('-') + 1] != '0';
    }

    private async Task<BoardSyncStatus> StatusAsync(string boardId, BoardSyncLinkRecord? link, CancellationToken cancellationToken)
    {
        var unsent = link is null ? 0 : await store.CountUnsentLogEntriesAsync(boardId, cancellationToken);
        var rejected = link is null ? 0 : await store.CountRejectedLogEntriesAsync(boardId, cancellationToken);
        var rejectedEntries = rejected == 0 ? [] : await store.GetRejectedLogEntriesAsync(boardId, 50, cancellationToken);
        var skipped = link is null ? 0 : await store.CountSkippedSyncEntriesAsync(boardId, cancellationToken);
        var skippedEntries = skipped == 0 ? [] : await store.GetSkippedSyncEntriesAsync(boardId, 50, cancellationToken);
        return new BoardSyncStatus(
            boardId,
            Published: link is not null,
            Enabled: link?.Enabled ?? false,
            link?.RemoteBoardId,
            BoardSyncEndpoint.BoardPage(ParserConfigs.GetFrontendUrl(), link?.RemoteBoardId),
            link?.Cursor ?? 0,
            unsent,
            link?.LastSyncUtc,
            link?.LastError,
            client.IsConfigured,
            rejected,
            rejectedEntries,
            skipped,
            skippedEntries);
    }

    private static string Trim(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
