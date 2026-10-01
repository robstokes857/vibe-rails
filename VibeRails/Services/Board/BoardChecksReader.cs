using VibeRails.DB;
using VibeRails.Utils;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed record BoardChecksResponse(IReadOnlyList<BoardCheckRecord> Checks,
    IReadOnlyList<BoardCheckRecord> Pending, IReadOnlyList<BoardCheckAutomation> Automations, bool HasMore,
    IReadOnlyList<BoardCheckRecord> Latest);
public sealed record BoardCheckAutomation(long Id, string Name, bool Enabled, IReadOnlyList<string> Scopes);
public sealed record BoardCheckReportResponse(BoardCheckRecord Check, string Freshness);

/// <summary>Joins local run liveness with durable evidence without altering either history.</summary>
public sealed class BoardChecksReader(IBoardStore boards, IJobStore jobs, BoardCheckService engine)
{
    public async Task<BoardChecksResponse?> ReadAsync(string project, string cardKey, int offset, CancellationToken ct)
    {
        var card = await boards.FindCardAsync(project, cardKey, ct);
        if (card is null) return null;
        if (offset is < 0 or > 1_000_000) throw new BoardValidationException("Invalid checks offset.");
        var checks = await boards.GetChecksAsync(project, card.Id, offset, ct);
        var resolved = new List<BoardCheckRecord>();
        foreach (var check in checks)
            resolved.Add(await ResolveAsync(check, ct));
        var latest = new List<BoardCheckRecord>();
        foreach (var check in await boards.GetLatestChecksAsync(project, card.Id, ct))
            latest.Add(await ResolveAsync(check, ct));
        var catalog = await jobs.GetJobsAsync(project, cancellationToken: ct);
        var options = catalog.Where(job => job.Actions?.Any(a => JobCheckScope.IsCheck(a.Kind)) == true)
            .Select(job => new BoardCheckAutomation(job.Id, job.Name, job.Enabled,
                job.Actions!.Where(a => JobCheckScope.IsCheck(a.Kind)).Select(a => string.Join(" ", a.Arguments)).ToList())).ToList();
        var pending = new List<BoardCheckRecord>();
        var active = (await jobs.GetActiveRunsAsync(ct)).Concat(await jobs.GetQueuedRunsAsync(ct));
        var recent = new List<JobRunRecord>();
        foreach (var reference in await boards.GetAgentRunsAsync(project, card.Id, ct))
            if (await jobs.GetRunAsync(reference.Id, ct) is { } run) recent.Add(run);
        foreach (var run in active.Concat(recent).DistinctBy(r => r.Id).OrderByDescending(r => r.QueuedUtc)
            .Where(r => ProjectPathComparer.Matches(r.ProjectPath, project)
                && JobBoardContext.GetCardKey(r.TriggerKind, r.TriggerKey) == card.Key))
        {
            var actions = await jobs.GetRunActionsAsync(run.Id, ct);
            foreach (var action in actions.Where(a => JobCheckScope.IsCheck(a.Kind)).OrderByDescending(a => a.Position))
            {
                var tool = action.Kind == JobActionKind.Vca ? "VCA" : "Code quality";
                if (checks.Concat(latest).Any(c => c.RunId == run.Id && c.ActionId == action.Id)
                    || latest.Any(c => c.Tool == tool && c.RunId != run.Id && c.StartedUtc >= run.QueuedUtc)) continue;
                var live = run.Status is JobRunStatus.Queued or JobRunStatus.Running;
                var status = live ? (action.Status == JobRunActionStatus.Pending ? "Waiting" : "Running")
                    : run.Status == JobRunStatus.Cancelled ? "Cancelled"
                    : action.Status == JobRunActionStatus.Skipped ? "Skipped/not applicable" : "Failed to run";
                pending.Add(new($"pending-{action.Id}", card.Id, run.Id, action.Id,
                    tool, status, string.Join(" ", action.Arguments), null, null, null, null, "unknown", run.QueuedUtc, run.EndedUtc,
                    live ? "Waiting for this Automation action to save evidence."
                        : $"No completed check evidence. Run {run.Status}: {action.ErrorMessage ?? run.ErrorMessage}", 0, 0, 0, 0, []));
            }
        }
        foreach (var entry in (await boards.GetLaneAutomationStatusesAsync(project, card.Id, ct)).Where(e => e.RunId is null))
        {
            var job = catalog.FirstOrDefault(job => job.Id == entry.JobId);
            foreach (var action in job?.Actions?.Where(a => JobCheckScope.IsCheck(a.Kind)) ?? [])
                pending.Add(new($"entry-{entry.EventKey}-{action.Id}", card.Id, "lane-entry", action.Id,
                    action.Kind == JobActionKind.Vca ? "VCA" : "Code quality", entry.Status,
                    string.Join(" ", action.Arguments), null, null, null, null, "unknown", entry.DueUtc, null,
                    entry.Reason, 0, 0, 0, 0, []));
        }
        return new(resolved, pending, options, checks.Count == 50, latest);
    }

    public async Task<BoardCheckReportResponse?> ReportAsync(string project, string cardKey, string id, CancellationToken ct, bool verify = false)
    {
        var card = await boards.FindCardAsync(project, cardKey, ct);
        if (card is null) return null;
        var check = await boards.GetCheckAsync(project, card.Id, id, ct);
        if (check is null) return null;
        return new(await ResolveAsync(check, ct), verify ? await engine.FreshnessAsync(project, check, ct) : "Unknown freshness — compare inputs to check this checkout.");
    }

    private async Task<BoardCheckRecord> ResolveAsync(BoardCheckRecord check, CancellationToken ct)
    {
        if (check.EndedUtc is not null) return check;
        var run = await jobs.GetRunAsync(check.RunId, ct);
        if (run is null) return check with { Status = "Unknown", Summary = "Run unavailable; completion is unknown." };
        return run.Status is JobRunStatus.Queued or JobRunStatus.Running ? check : check with
        {
            Status = run.Status == JobRunStatus.Cancelled ? "Cancelled" : "Failed to run",
            Summary = $"Run ended ({run.Status}) before check evidence was completed. {run.ErrorMessage}"
        };
    }
}
