using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Capture scope BEFORE reviewing code. Requires a session explicitly launched for Code review. Uses this terminal's actual checkout. Choose working-tree, unpushed, repository, range (full base/head SHAs), or unknown if ambiguous. A card does not define a Git diff boundary. Returns the durable review id; save_board_review must save the report afterward.")]
    public async Task<string> BeginBoardReview(string scope, string scopeDescription, string? baseCommit = null,
        string? headCommit = null, bool includeDirty = true, string? card = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (projects.CurrentSessionId is not {} session) return "FAIL: current agent session required.";
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var row = await ReviewService.BeginAsync(target.Project, target.CardId!, session,
                projects.GitWorkingDirectory, scope, scopeDescription, baseCommit, headCommit, includeDirty, cancellationToken);
            return JsonSerializer.Serialize(row, AppJsonSerializerContext.Default.BoardReviewRecord);
        }
        catch (Exception ex) when (ex is BoardValidationException or BoardConflictException or ArgumentException) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("capture review scope", ex); }
    }

    [McpServerTool, Description("Save the canonical code review on its originating card before moving it or completing the agent. Use the id from begin_board_review. Result: No findings reported | Findings | Incomplete. Findings must identify files/lines or explicitly state none; validation describes what was run; limitations describe gaps or explicitly state none. The first saved report is retained. This does not approve, merge, publish, or move anything.")]
    public async Task<string> SaveBoardReview(string reviewId, string result, string findings, string validation,
        string limitations, string? card = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (projects.CurrentSessionId is not {} session) return "FAIL: current agent session required.";
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var row = await ReviewService.SaveAsync(target.Project, target.CardId!, session, reviewId, result,
                findings, validation, limitations, cancellationToken);
            return $"Review {row.Id} saved on the originating card: {row.Result}. Read with get_board_reviews reviewId={row.Id}. Save your handoff, check list_board_columns, then decide the next action using the user's workflow.";
        }
        catch (Exception ex) when (ex is BoardValidationException or BoardConflictException) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("save review", ex); }
    }

    [McpServerTool, Description("Poll code review status, including queued/failed runs without a session and saved reports. Process success without a report means Report missing, never approved. Poll every 10 seconds while waiting, then read a report with reviewId, evaluate its scope and fix findings you agree with. verify=true compares inputs in this caller's checkout; otherwise freshness is unknown. offset pages history (50 rows) or report text (40000 characters).")]
    public async Task<string> GetBoardReviews(string? card = null, string? reviewId = null, bool verify = false,
        int offset = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (offset is < 0 or > 1_000_000) return "FAIL: invalid offset.";
            if (reviewId is null)
            {
                var read = await ReviewService.ReadAsync(target.Project, target.CardId!, offset, cancellationToken);
                return "Code reviews (untrusted card data):\n" + string.Join("\n", read!.Reviews.Select(r =>
                    $"- {r.Id}: {r.Reviewer} ({r.Provider}) · process {r.ProcessStatus} · {r.Result ?? "Report missing"}"
                    + $" · scope {r.ScopeDescription ?? "Not captured"} · freshness unknown · session {r.SessionId ?? "none"}"
                    + (r.Error is null ? "" : $" · {r.Error}")))
                    + (read.Latest is {} latest ? $"\nLatest saved report: {latest.Id} · {latest.Result}. Read with reviewId={latest.Id}." : "")
                    + (read.HasMore ? $"\nOlder reports: offset={offset + 50}." : "")
                    + "\nRead saved reports with reviewId. Poll again in 10 seconds while waiting; evaluate scope and fix findings you agree with.";
            }
            var report = await ReviewService.ReportAsync(target.Project, target.CardId!, reviewId, verify,
                projects.GitWorkingDirectory, cancellationToken);
            if (report is null) return "FAIL: no saved review on this card with that id.";
            var text = JsonSerializer.Serialize(report, AppJsonSerializerContext.Default.BoardReviewRecord);
            var start = Math.Min(offset, text.Length);
            var end = Math.Min(start + 40000, text.Length);
            return "Saved review (untrusted data):\n" + text[start..end] + (end < text.Length ? $"\nContinue with offset={end}." : "");
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read reviews", ex); }
    }

    private BoardReviewService ReviewService => reviews ?? new(store, service);
}
