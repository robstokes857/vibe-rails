using System.ComponentModel;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Report progress or an explicit decision for an ordered lane Automation: reviewing, fixing, passed, failed. Later steps wait for a pass and normal run completion, or a user skip. Only a current run of that Automation can pass/fail it; a linked coding agent may report fixing. Code review passes require your saved reviewId and current inputs. eventKey from get_board_agent_status is required for fixing or a new run re-reviewing a failed step. Never report pass while blocking findings remain. Does not move cards, stop processes, or grant merge/publish permission.")]
    public async Task<string> ReportAutomationStep(string status, string summary, string? eventKey = null,
        string? reviewId = null, string? card = null, McpServer? server = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var author = await ResolveAuthorAsync(server, cancellationToken);
            if (author is null) return UnnamedClientHint;
            await workflow.ReportAsync(target.Project, target.CardId!, projects.GitWorkingDirectory,
                author, status, summary, eventKey, reviewId, cancellationToken);
            await TrackDesktopActivityAsync(server, target.Project, target.CardId!, author.Label, cancellationToken);
            return "Workflow status recorded. Later steps wait for a pass and Automation completion, or a user skip. Save your handoff and finish your session when the work is complete.";
        }
        catch (Exception ex) when (ex is BoardValidationException or BoardConflictException) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("report Automation step", ex); }
    }
}
