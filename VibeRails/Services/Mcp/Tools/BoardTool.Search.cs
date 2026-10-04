using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    /// <summary>Searches the same local Board corpus used by the dashboard and link picker.</summary>
    [McpServerTool, Description("Search cards across ALL local boards using semantic and keyword matches over titles, descriptions, comments, legacy notes and previous-work handoffs. Current-repository matches are preferred. Results identify the board, lane and project, and explicitly warn about other repositories. Use the returned permanent key or row ID with get_board_card/update_board_card; display IDs may repeat across projects.")]
    public async Task<string> SearchBoardCards(
        [Description("Natural-language query, exact card key, display ID or row ID; at most 1000 characters.")] string query,
        [Description("Maximum matches, from 1 to 50; defaults to 20.")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return "FAIL: query cannot be empty.";
            if (query.Length > 1000) return "FAIL: query is limited to 1000 characters.";
            if (search is null) return "FAIL: Board search is unavailable in this host.";
            var project = await projects.ResolveAsync(cancellationToken);
            var hits = await search.SearchAsync(project, query, Math.Clamp(maxResults, 1, 50), ct: cancellationToken);
            if (hits.Count == 0) return "No cards match across the local boards.";
            var output = new StringBuilder("Local Board search (current repository preferred):\n");
            foreach (var hit in hits)
            {
                output.AppendLine($"{BoardPromptComposer.SanitizeLine(hit.DisplayId, 80)} ({hit.Key}) — id {hit.Id}")
                    .AppendLine($"  {BoardPromptComposer.SanitizeLine(hit.Title, 300)}")
                    .AppendLine($"  Board: {BoardPromptComposer.SanitizeLine(hit.BoardName, 80)} [{hit.BoardId}] · Lane: {BoardPromptComposer.SanitizeLine(hit.ColumnName, 80)}")
                    .AppendLine($"  {(hit.IsCurrentProject ? "Current repository" : "WARNING: another repository")}: {BoardPromptComposer.SanitizeLine(hit.ProjectPath, 1000)}")
                    .AppendLine($"  {BoardPromptComposer.SanitizeLine(hit.Snippet, 300)}");
            }
            output.Append("Read or edit using the permanent key or row ID above; short/display IDs resolve in the current project.");
            return output.ToString();
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("search the local Board cards", ex); }
    }
}
