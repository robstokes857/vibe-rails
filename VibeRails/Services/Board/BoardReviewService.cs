using System.Security.Cryptography;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.Board;

/// <summary>A bounded page of attempts and the latest saved report, independent of pagination.</summary>
public sealed record BoardReviewsResponse(IReadOnlyList<BoardReviewRecord> Reviews, bool HasMore, BoardReviewRecord? Latest = null);

/// <summary>Shared review result and scope contract for REST and MCP. Never moves a card.</summary>
public sealed class BoardReviewService(IBoardStore store, IBoardService board)
{
    private readonly GitStagedSnapshotProvider snapshots = new();

    /// <summary>Read review attempts and process observations without interpreting process success as approval.</summary>
    public async Task<BoardReviewsResponse?> ReadAsync(string project, string cardKey, int offset, CancellationToken ct)
    {
        if (offset is < 0 or > 1_000_000) throw new BoardValidationException("Invalid review offset.");
        var card = await store.FindCardAsync(project, cardKey, ct);
        if (card is null) return null;
        var saved = await store.GetReviewsAsync(project, card.Id, offset, ct);
        var runs = await store.GetReviewRunsAsync(project, card.Id, offset, ct);
        var rows = saved.ToList();
        foreach (var run in runs.Where(r => r.Purpose == "code_review"))
                if (!rows.Any(r => r.RunId == run.Id))
                    rows.Add(await store.GetReviewAsync(project, card.Id, run.Id, ct) ?? FromRun(card.Id, run));
        if (offset == 0)
        {
            var entries = await store.GetLaneAutomationStatusesAsync(project, card.Id, ct);
            foreach (var entry in entries.Where(e => e.Purpose == "code_review" && e.RunId is null))
            {
                rows.Add(new($"entry-{entry.EventKey}", card.Id, "unknown", entry.Name,
                    entry.DueUtc, ProcessStatus: entry.Status, Error: entry.Reason));
            }
        }
        var resolved = new List<BoardReviewRecord>();
        foreach (var row in rows.OrderByDescending(r => r.CreatedUtc))
        {
            var run = runs.FirstOrDefault(r => r.Id == row.RunId);
            if (run is null && row.RunId is not null)
                run = await store.FindReviewRunAsync(project, card.Id, row.RunId, null, ct);
            var updated = run is null ? (row.RunId is null ? row : row with { ProcessStatus = "Run status unknown" }) : row with { ProcessStatus = run.Status.ToString(), Error = run.Error,
                SessionId = run.WorkerSessionId ?? row.SessionId, TerminalSessionId = run.SessionId ?? row.TerminalSessionId };
            if (updated.RunId is null && updated.SessionId is {} session)
            {
                var outcome = await board.FindSessionOutcomeAsync(session, ct);
                var status = (await board.GetAgentSessionsAsync(project, card.Id, session, ct)).FirstOrDefault();
                updated = updated with { ProcessStatus = outcome?.EndedUtc is not null ? $"Exited ({outcome.ExitCode?.ToString() ?? "unknown"})"
                    : status?.Active == true ? "Running" : "Liveness unknown" };
            }
            resolved.Add(updated with { Freshness = "Unknown — compare review inputs" });
        }
        var latest = await store.GetLatestReviewAsync(project, card.Id, ct);
        if (latest is not null) latest = resolved.FirstOrDefault(r => r.Id == latest.Id)
            ?? latest with { ProcessStatus = "Process status unknown", Freshness = "Unknown — compare review inputs" };
        return new(resolved, saved.Count == 50 || runs.Count == 50, latest);
    }

    internal static BoardReviewRecord FromRun(string cardId, BoardAgentRun run) => new(
        run.Id, cardId, run.Provider, run.Reviewer ?? run.Name, run.QueuedUtc,
        RunId: run.Id, SessionId: run.WorkerSessionId, ProcessStatus: run.Status.ToString(), Error: run.Error,
        TerminalSessionId: run.SessionId);

    /// <summary>Capture before reading code, using only the caller's server-derived checkout.</summary>
    public async Task<BoardReviewRecord> BeginAsync(string project, string cardId, string session, string workspace,
        string scope, string description, string? baseCommit, string? headCommit, bool includeDirty, CancellationToken ct)
    {
        RequireText(description, "Scope description", 2000);
        if (!(await store.GetAgentSessionsAsync(project, cardId, session, ct)).Any())
            throw new BoardValidationException("This session is not linked to the review card.");
        var row = await store.GetReviewForSessionAsync(project, cardId, session, ct);
        if (row is null && await store.FindReviewRunAsync(project, cardId, null, session, ct) is {} run)
            row = FromRun(cardId, run);
        if (row is null) throw new BoardValidationException("This session was not launched with Code review purpose for this card.");
        if (row.ReportedUtc is not null || row.CapturedUtc is not null) return row;
        if (scope != "unknown") JobCheckScope.Parse(scope == "range" ? [scope, baseCommit ?? "", headCommit ?? ""] : [scope]);
        var capture = await CaptureAsync(workspace, scope, baseCommit, headCommit, includeDirty, ct);
        row = row with { Workspace = workspace, Scope = scope, ScopeDescription = description.Trim(),
            BaseCommit = capture.Base, HeadCommit = capture.Head, IncludeDirty = includeDirty,
            SnapshotHash = capture.Hash, CaptureLimitations = capture.Limitations, CapturedUtc = DateTime.UtcNow, ScopeFiles = capture.Files };
        if (!await store.SaveReviewAsync(project, row, ct)) throw new BoardConflictException("Review changed or card is no longer available.");
        return row;
    }

    /// <summary>Finalize the owning session's first durable report and its discussion reference.</summary>
    public async Task<BoardReviewRecord> SaveAsync(string project, string cardId, string session, string id,
        string result, string findings, string validation, string limitations, CancellationToken ct)
    {
        if (result is not ("No findings reported" or "Findings" or "Incomplete"))
            throw new BoardValidationException("Result must be No findings reported, Findings or Incomplete.");
        RequireText(findings, "Findings (with file references, or an explicit none)", 20000);
        RequireText(validation, "Validation performed", 6000);
        RequireText(limitations, "Limitations (or an explicit none)", 6000);
        var row = await store.GetReviewAsync(project, cardId, id, ct)
            ?? throw new BoardValidationException("Begin the review before saving its report.");
        if (row.SessionId != session || !(await store.GetAgentSessionsAsync(project, cardId, session, ct)).Any())
            throw new BoardValidationException("Only this review's linked agent may save its report.");
        if (row.ReportedUtc is not null) return row;
        if (row.CapturedUtc is null) throw new BoardValidationException("Capture review scope before saving.");
        if (row.Scope == "unknown") result = "Incomplete";
        row = row with { Result = result, Findings = findings.Trim(), Validation = validation.Trim(),
            Limitations = limitations.Trim(), ReportedUtc = DateTime.UtcNow };
        if (!await store.SaveReviewAsync(project, row, ct)) throw new BoardConflictException("Review already saved or card unavailable.");
        return row;
    }

    /// <summary>Read canonical evidence, optionally comparing inputs in a server-derived checkout.</summary>
    public async Task<BoardReviewRecord?> ReportAsync(string project, string cardKey, string id, bool verify, string comparisonWorkspace, CancellationToken ct)
    {
        var card = await store.FindCardAsync(project, cardKey, ct);
        if (card is null) return null;
        var row = await store.GetReviewAsync(project, card.Id, id, ct);
        if (row is null || !verify) return row;
        // Do not follow a persisted/caller-supplied path. Another checkout cannot establish freshness.
        if (!VibeRails.Utils.ProjectPathComparer.Matches(row.Workspace, comparisonWorkspace)
            || row.SnapshotHash is null || row.ReportedUtc is null)
            return row with { Freshness = "Unknown — unavailable inputs or different checkout" };
        var current = await CaptureAsync(comparisonWorkspace, row.Scope!, row.BaseCommit, row.HeadCommit, row.IncludeDirty, ct);
        return row with { Freshness = current.Hash is null ? "Unknown — inputs unavailable"
            : current.Hash == row.SnapshotHash ? "Current — captured inputs match" : "Stale — reviewed changes differ now" };
    }

    private sealed record Capture(string? Hash, string? Base, string? Head, string? Limitations, IReadOnlyList<string>? Files = null);

    private async Task<Capture> CaptureAsync(string workspace, string scope, string? baseCommit, string? headCommit, bool dirty, CancellationToken ct)
    {
        if (scope == "unknown") return new(null, baseCommit, headCommit, "Scope ambiguous; freshness cannot be established.");
        try
        {
            IReadOnlyList<string>? files = null;
            async Task<(string? Hash, string? Base, string? Head)> Once()
            {
                var head = await GitCli.RunAsync(workspace, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
                var snapshot = scope switch
                {
                    "range" => await snapshots.CaptureRangeAsync(workspace, baseCommit!, headCommit!, ct),
                    "unpushed" => await snapshots.CaptureUnpushedAsync(workspace, ct),
                    "repository" => await snapshots.CaptureRepositoryAsync(workspace, ct),
                    _ => await snapshots.CaptureWorkingTreeAsync(workspace, ct)
                };
                var extra = dirty && scope != "working-tree" ? await snapshots.CaptureWorkingTreeAsync(workspace, ct) : null;
                files = snapshot.Files.Select(f => $"{scope}: {f.ChangeKind} {f.RelativePath}")
                    .Concat(extra?.Files.Select(f => $"dirty: {f.ChangeKind} {f.RelativePath}") ?? []).Take(1000).ToList();
                // Binary/omitted contents cannot establish a trustworthy content identity.
                if (snapshot.Files.Concat(extra?.Files ?? []).Any(f => f.IsBinary || (f.ExistsInIndex && f.Content is null)))
                    return (null, snapshot.CheckIdentity?.BaseCommit, snapshot.CheckIdentity?.HeadCommit ?? head.StdOut.Trim());
                var value = head.StdOut.Trim() + BoardCheckService.Fingerprint(snapshot)
                    + (extra is null ? "" : BoardCheckService.Fingerprint(extra));
                return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
                    snapshot.CheckIdentity?.BaseCommit ?? (head.Succeeded ? head.StdOut.Trim() : null),
                    snapshot.CheckIdentity?.HeadCommit ?? (head.Succeeded ? head.StdOut.Trim() : null));
            }
            var first = await Once();
            var second = await Once();
            return first.Hash is not null && first == second ? new(first.Hash, first.Base, first.Head, files?.Count == 1000 ? "File listing capped at 1,000; fingerprint covers captured inputs." : null, files)
                : new(null, first.Base, first.Head, "Inputs changed during capture, or binary/omitted content prevents complete freshness verification.", files);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(null, baseCommit, headCommit, "Scope capture unavailable; describe what was actually reviewed and its limitations."); }
    }

    private static void RequireText(string value, string label, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new BoardValidationException($"{label} requires 1–{max} characters.");
    }
}
