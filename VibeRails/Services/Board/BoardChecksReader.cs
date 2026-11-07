using VibeRails.DB;
using VibeRails.Utils;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed record BoardChecksResponse(IReadOnlyList<BoardCheckRecord> Checks,
    IReadOnlyList<BoardCheckRecord> Pending, IReadOnlyList<BoardCheckAutomation> Automations, bool HasMore,
    IReadOnlyList<BoardCheckRecord> Latest);
public sealed record BoardCheckAutomation(long Id, string Name, bool Enabled, IReadOnlyList<string> Scopes, string? DefinitionId = null);
public sealed record BoardCheckReportResponse(BoardCheckRecord Check, string Freshness);

/// <summary>Joins local run liveness with durable evidence without altering either history.</summary>
public sealed class BoardChecksReader(IBoardStore boards, BoardCheckService engine, VibeRails.Launches.ILaunchStore? launches = null)
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
        var catalog = launches is null ? [] : await launches.ListActionsAsync(ct);
        var options = catalog.Where(a => a.Check is not null && a.RetiredUtc is null)
            .Select(a => new BoardCheckAutomation(0, a.Name, a.Enabled, [a.Check!.Scope], a.Id)).ToList();
        var pending = new List<BoardCheckRecord>();
        var runs = launches is null ? [] : await launches.GetBoardRunsAsync(VibeRails.Services.Launches.LaunchService.ProjectKey(project), card.Id, ct: ct);
        foreach (var run in runs.Where(r => r.Snapshot.Action?.Check is not null))
        {
            var action = run.Snapshot.Action!;
            if (checks.Concat(latest).Any(c => c.RunId == run.Id)) continue;
            pending.Add(new($"pending-{run.Id}", card.Id, run.Id, action.Id,
                action.Kind == "vca" ? "VCA" : "Code quality", run.Status == "cancelled" ? "Cancelled" : run.IsTerminal ? "Failed to run" : run.Status == "queued" ? "Waiting" : "Running",
                action.Check!.Scope, null, null, null, null, "unknown", run.QueuedUtc, run.EndedUtc,
                run.Error ?? "Waiting for saved check evidence.", 0, 0, 0, 0, []));
        }
        foreach (var entry in (await boards.GetLaunchEventsAsync(project, card.Id, ct)).Where(e => e.RunId is null))
        {
            var action = catalog.FirstOrDefault(a => a.Id == entry.DefinitionId && a.Check is not null);
            if (action is null) continue;
            pending.Add(new($"entry-{entry.Id}", card.Id, "lane-entry", action.Id,
                action.Kind == "vca" ? "VCA" : "Code quality", entry.SkipRequested ? "Skipped" : entry.Current ? "Waiting" : "Cancelled",
                action.Check!.Scope, null, null, null, null, "unknown", entry.DueUtc, null,
                entry.Summary ?? "Waiting for the lane entry.", 0, 0, 0, 0, []));
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
        var run = launches is null ? null : await launches.GetRunAsync(check.RunId, ct);
        if (run is null) return check with { Status = "Unknown", Summary = "Run unavailable; completion is unknown." };
        return !run.IsTerminal ? check : check with
        {
            Status = run.Status == "cancelled" ? "Cancelled" : "Failed to run",
            Summary = $"Run ended ({run.Status}) before check evidence was completed. {run.Error}"
        };
    }
}
