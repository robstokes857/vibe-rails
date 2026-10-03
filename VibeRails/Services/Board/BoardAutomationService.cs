using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>Project-scoped lane settings and existing Automation choices for the dashboard.</summary>
public sealed class BoardAutomationService(IBoardStore boards, IJobStore jobs,
    BoardStarterWorkflowService? starters = null, IRepository? repository = null, IJobExecutableResolver? executables = null)
{
    public async Task<BoardLaneAutomationResponse?> GetAsync(string projectPath, string columnId, CancellationToken cancellationToken)
    {
        if (starters is not null) await starters.RecoverAsync(projectPath, cancellationToken);
        var setting = await boards.GetLaneAutomationAsync(projectPath, columnId, cancellationToken);
        if (setting is null) return null;
        var catalog = await jobs.GetJobsAsync(projectPath, cancellationToken: cancellationToken);
        var choices = new List<BoardAutomationOption>();
        foreach (var job in catalog)
            choices.Add(new(job.Id, job.Name, job.Enabled, setting.JobIds.Contains(job.Id)
                ? await ReviewSetupAsync(projectPath, job, cancellationToken) : null));
        var pending = (await boards.GetPendingStarterWorkflowsAsync(projectPath, cancellationToken)).Any(seed => seed.ColumnId == columnId);
        return new(setting.JobId, setting.Revision, choices, setting.JobIds, pending, await ReadRunningAgentsAsync(projectPath, columnId, cancellationToken));
    }

    /// <summary>
    /// The lane panel's poll: Automations running for cards that entered this lane and are still on its board.
    /// No catalog, starter recovery or reviewer setup probes, which only the full settings read needs.
    /// </summary>
    public async Task<BoardLaneRunningAgentsResponse?> GetRunningAgentsAsync(string projectPath, string columnId, CancellationToken cancellationToken) =>
        await boards.GetColumnAsync(projectPath, columnId, cancellationToken) is null ? null
            : new(await ReadRunningAgentsAsync(projectPath, columnId, cancellationToken));

    private async Task<IReadOnlyList<BoardLaneRunningAgent>> ReadRunningAgentsAsync(string projectPath, string columnId, CancellationToken cancellationToken)
    {
        // The store resolves each run's card by its stored trigger key and keeps the entry lane only while it is on
        // the card's current board, so a legacy short key is matched exactly instead of failing alias resolution.
        var entries = (await boards.GetRunningAutomationsAsync(projectPath, cancellationToken))
            .Where(entry => entry.ColumnId == columnId && entry.RunId is not null).ToList();
        if (entries.Count == 0) return [];
        var runs = (await jobs.GetActiveRunsAsync(cancellationToken)).ToDictionary(run => run.Id, StringComparer.Ordinal);
        var running = new List<BoardLaneRunningAgent>();
        foreach (var entry in entries)
        {
            if (!runs.TryGetValue(entry.RunId!, out var run)) continue;
            // Only this lane's running cards are read, by row ID.
            var card = await boards.FindCardAsync(projectPath, entry.CardId, cancellationToken);
            if (card is null) continue;
            running.Add(new(run.Id, run.JobName, card.Id, $"{card.DisplayId ?? card.Key} · {card.Title}", run.SessionId, run.TerminalSessionId));
        }
        return running;
    }

    private async Task<string?> ReviewSetupAsync(string project, JobDefinitionRecord job, CancellationToken ct)
    {
        var workerId = job.Actions?.FirstOrDefault(a => a.Kind == JobActionKind.Worker)?.EnvironmentId ?? job.EnvironmentId;
        if (workerId is null || repository is null || executables is null) return null;
        var worker = await repository.GetEnvironmentByIdAsync(workerId.Value, ct);
        if (worker?.Purpose != "code_review") return null;
        var targets = worker.ReviewerRouting?.Mode == "switch"
            ? worker.ReviewerRouting.Mappings.Select(m => m.Reviewer).Append(worker.ReviewerRouting.Fallback)
            : [new ReviewerTarget(BoardSelection.Format(worker.LLM, worker.Id))];
        var problems = new List<string>();
        foreach (var target in targets.DistinctBy(t => t.Selection))
        {
            if (!BoardSelection.TryParse(target.Selection, out var selection)) continue;
            if (selection!.EnvironmentId is int id)
            {
                var environment = await repository.GetEnvironmentByIdAsync(id, ct);
                if (environment is null || environment.LLM != selection.Llm || !ProjectPathComparer.IsVisibleIn(environment.ProjectPath, project))
                    problems.Add($"Restore or replace reviewer {target.Selection}.");
            }
            if (executables.Resolve(selection.Llm) is null)
                problems.Add($"Install/sign in to {selection.Cli} before cards routed to it can run.");
        }
        return problems.Count > 0 ? "Setup needed: " + string.Join(" ", problems.Distinct()) + " No provider will be substituted."
            : "Reviewer CLIs found. Sign in to each selected provider before running; credentials and scope are checked at launch.";
    }

    public Task<BoardLaneAutomation?> SaveAsync(string projectPath, string columnId, UpdateBoardLaneAutomationRequest request, CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision is null)
            throw new BoardValidationException("The expected lane automation revision is required.");
        if (request.JobIds is not null && request.JobId is not null)
            throw new BoardValidationException("Send jobIds or jobId, not both.");
        var jobIds = request.JobIds ?? (request.JobId is long jobId ? [jobId] : Array.Empty<long>());
        return boards.SaveLaneAutomationAsync(projectPath, columnId, jobIds, request.ExpectedRevision.Value, cancellationToken);
    }
}
