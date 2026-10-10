using System.Text.Json;
using VibeRails.DB;
using VibeRails.Services.Board.Sync;

namespace VibeRails.Services.Board.Sharing;

/// <summary>Exports the saved card, never arbitrary paths or live checkout contents.</summary>
public sealed class CardShareCapture(IBoardStore boards, ISessionStore sessions, IChatSummaryStore summaries)
{
    public const int MaxBytes = 64 * 1024 * 1024;
    public async Task<CardShareSnapshot> CaptureAsync(string project, string identity, CancellationToken ct)
    {
        var card = await boards.FindCardAsync(project, identity, ct) ?? throw new BoardValidationException("Card not found.");
        var metadata = await boards.GetSyncActivityAsync(project, card.BoardId, card.Id, ct)
            ?? throw new BoardValidationException("Card not found.");
        if (metadata.Attachments.Any(a => a.Bytes < 0 || a.Bytes > MaxBytes)
            || metadata.Attachments.Sum(a => a.Bytes) > MaxBytes * 3L / 4)
            throw TooLarge();
        var detail = await boards.GetCardDetailAsync(project, card.Id, ct) ?? throw new BoardValidationException("Card not found.");
        var board = await boards.GetBoardAsync(project, card.BoardId, ct);
        var lane = await boards.GetColumnAsync(project, card.ColumnId, ct);
        var files = new List<CardShareAttachment>();
        long budget = 0;
        foreach (var file in detail.Attachments)
        {
            if (file.Bytes < 0 || file.Bytes > MaxBytes || (budget += ((file.Bytes + 2) / 3 * 4)) > MaxBytes) throw TooLarge();
            var loaded = await boards.GetAttachmentContentAsync(project, card.Id, file.Id, ct)
                ?? throw new BoardValidationException("An attachment changed while preparing the share. Try again.");
            if (loaded.Content.LongLength != file.Bytes) throw new BoardValidationException("An attachment changed while preparing the share. Try again.");
            files.Add(new(file.Name, file.MimeType, file.Bytes, file.CreatedUtc, Convert.ToBase64String(loaded.Content)));
        }
        var commits = new List<CardShareCommit>();
        foreach (var commit in detail.Commits)
        {
            var saved = await boards.GetCommitSnapshotAsync(project, card.Id, commit.Sha, ct);
            var content = saved?.Files.Select(f => new CardShareFile(f.FileName, f.OriginalContent, f.ModifiedContent)).ToList() ?? [];
            budget += content.Sum(f => (long)f.Before.Length + f.After.Length);
            if (budget > MaxBytes) throw TooLarge();
            commits.Add(new(commit.Sha, commit.Author, commit.Message, commit.CommittedUtc, content,
                saved is null ? "This commit has no saved local diff." : null));
        }
        var recordings = new List<CardShareSession>();
        foreach (var session in detail.Sessions)
        {
            if (!Guid.TryParse(session.SessionId, out var id)) throw new BoardValidationException("A linked session has an invalid recording identity.");
            var outcome = await sessions.GetSessionByIdAsync(session.SessionId, ct);
            var summary = string.Join("\n\n", (await summaries.GetChatSummariesBySessionAsync(session.SessionId, ct))
                .OrderBy(s => s.Date).Select(s => s.SummaryText));
            recordings.Add(new(id, session.DisplayName, session.Cli, session.Origin, session.CreatedUtc,
                outcome?.EndedUTC, outcome?.ExitCode, string.IsNullOrEmpty(summary) ? null : summary));
        }
        var evidence = new List<CardShareEvidence>();
        if (detail.PreviousWork is { } handoff)
            evidence.Add(new("handoff", "Previous work", "Saved", handoff.CreatedUtc ?? card.UpdatedUtc,
                $"Outcome\n{handoff.Outcome}\n\nDecisions\n{handoff.Decisions}\n\nValidation\n{handoff.Validation}\n\nOutstanding\n{handoff.Outstanding}\n\nFiles\n"
                + string.Join("\n", handoff.Files.Select(f => $"{f.Path}: {f.Reason}"))));
        for (var offset = 0; ; offset += 50)
        {
            var reviews = await boards.GetReviewsAsync(project, card.Id, offset, ct);
            foreach (var review in reviews)
            {
                var full = await boards.GetReviewAsync(project, card.Id, review.Id, ct) ?? review;
                evidence.Add(new("review", full.Reviewer + " review", full.Result ?? full.ProcessStatus, full.CreatedUtc,
                    $"Scope: {full.ScopeDescription ?? full.Scope}\nBase: {full.BaseCommit}\nHead: {full.HeadCommit}\n\nFindings\n{full.Findings}\n\nValidation\n{full.Validation}\n\nLimitations\n{full.Limitations}\n{full.CaptureLimitations}"));
            }
            if (reviews.Count < 50) break;
            if (evidence.Count > 100000) throw TooLarge();
        }
        for (var offset = 0; ; offset += 50)
        {
            var checks = await boards.GetChecksAsync(project, card.Id, offset, ct);
            foreach (var check in checks)
            {
                var full = await boards.GetCheckAsync(project, card.Id, check.Id, ct) ?? check;
                evidence.Add(new("check", full.Tool, full.Status, full.StartedUtc,
                    $"Scope: {full.Scope}\nBase: {full.BaseCommit}\nHead: {full.HeadCommit}\n\n{full.Summary}\n\n{string.Join("\n", full.Limitations)}\n\n{full.ResultJson}"));
            }
            if (checks.Count < 50) break;
            if (evidence.Count > 100000) throw TooLarge();
        }
        static CardShareEntry Entry(BoardCommentRecord c) => new(c.Author.Label, c.Author.Purpose, c.Body, c.CreatedUtc);
        var snapshot = new CardShareSnapshot(1, card.Key, card.DisplayId, card.Title, card.Description,
            board?.Name ?? "Board", lane?.Name ?? "", card.Type, card.Priority,
            BoardSelection.TryParse(card.Assignee, out var assignee) ? "base:" + assignee!.Cli : null, card.Points, card.Tags,
            card.Blocked, card.Flagged, card.CreatedUtc, card.UpdatedUtc,
            detail.Comments.Concat(detail.Notes).DistinctBy(c => c.Id).OrderBy(c => c.CreatedUtc).Select(Entry).ToList(),
            (await boards.GetCardHistoryAsync(project, card.Id, ct)).Select(c => HistoryEntry(c, card.Key)).ToList(), recordings, commits, files,
            detail.LinkedCards.Select(c => new CardShareRelated(c.Key, c.DisplayId, c.Title)).ToList(), evidence);
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, CardSharingJsonContext.Default.CardShareSnapshot).Length > MaxBytes - 1024) throw TooLarge();
        var current = await boards.FindCardAsync(project, card.Id, ct);
        if (current is null || current.UpdatedUtc != card.UpdatedUtc)
            throw new BoardValidationException("The card changed while preparing the share. Try again.");
        return snapshot;
    }
    private static CardShareEntry HistoryEntry(BoardCommentRecord entry, string cardKey)
    {
        // Use the existing portable-field projection, including its assignee normalization.
        // Raw change JSON and generated summaries can contain local launch configuration.
        var portable = BoardSyncService.ToWire(new(entry, cardKey));
        var body = portable.Body;
        if (portable.Changes is { ValueKind: JsonValueKind.Object } changes)
        {
            foreach (var field in changes.EnumerateObject())
            {
                body += "\n\n" + field.Name;
                foreach (var value in field.Value.EnumerateObject())
                    body += "\n" + value.Name + ": " + (value.Value.ValueKind == JsonValueKind.String
                        ? value.Value.GetString() : value.Value.GetRawText());
            }
        }
        return new(entry.Author.Label, entry.Author.Purpose, body, entry.CreatedUtc);
    }
    private static BoardValidationException TooLarge() => new("The complete card exceeds the 64 MiB sharing transfer limit. No partial card was published.");
}
