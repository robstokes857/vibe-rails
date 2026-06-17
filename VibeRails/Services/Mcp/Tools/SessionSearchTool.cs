using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Semantic + lexical search over the developer's captured agent session history, backed
/// by the same BGE-small-en / sqlite-vec / FTS5 + RRF-fusion stack that powers the "Vibe AI"
/// inspector (<see cref="IUnifiedSearchService"/>). Exposed over the in-process HTTP MCP
/// transport at /mcp; MCP normalizes the method name to <c>search_history</c>.
///
/// This is an instance tool: the MCP server resolves it (and its injected
/// <see cref="IUnifiedSearchService"/>) from the per-request DI scope. See the matching
/// AddScoped registration in MapRegisterServices.
/// </summary>
[McpServerToolType]
public class SessionSearchTool
{
    private readonly IUnifiedSearchService _search;

    public SessionSearchTool(IUnifiedSearchService search)
    {
        _search = search;
    }

    [McpServerTool]
    [Description(
        "Search the developer's own captured agent history — past user messages and whole-session " +
        "summaries from previous Claude/Codex/Copilot/Antigravity sessions in this workspace. Use this " +
        "to recall what was previously asked, decided, tried, or fixed before redoing work or asking the " +
        "user. Results are ranked by reciprocal-rank fusion across semantic (BGE embedding) and literal " +
        "keyword matches, best first.")]
    public string SearchHistory(
        [Description("Natural-language query, e.g. 'how did we fix the websocket reconnect timeout'.")] string query,
        [Description("Maximum number of results to return (default 10, capped at 50).")] int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "FAIL: query cannot be empty.";
        }

        UnifiedSearchResponse response;
        try
        {
            response = _search.Search(query, maxResults);
        }
        catch (ArgumentException ex)
        {
            return $"FAIL: {ex.Message}";
        }

        // The fused group is RRF across per-message-semantic, per-session-semantic and lexical —
        // the single best-overall ranking, which is what an agent wants.
        var fused = response.Groups.FirstOrDefault(g => g.Key == "fused");
        var hits = fused?.Hits ?? new List<BertSearchHitResponse>();

        if (hits.Count == 0)
        {
            return $"No matches for \"{query}\" in the captured session history.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Top {hits.Count} match(es) for \"{query}\" (fused semantic + keyword ranking):");
        sb.AppendLine();

        var rank = 1;
        foreach (var hit in hits)
        {
            var when = hit.TimestampUTC?.ToString("u") ?? "unknown time";
            var kind = hit.Kind == "session-chunk" ? "session summary" : "message";
            var locus = hit.Kind == "session-chunk"
                ? (hit.ChunkIndex is int chunk ? $"chunk {chunk}" : "chunk")
                : (hit.Sequence is int seq ? $"#{seq}" : "msg");

            sb.AppendLine($"{rank}. [{hit.Cli ?? "?"}] {kind} {locus} · {when} · session {hit.SessionId}");
            sb.AppendLine($"   {Collapse(hit.UserTextPreview)}");
            sb.AppendLine();
            rank++;
        }

        return sb.ToString().TrimEnd();
    }

    // Flatten newlines/runs of whitespace so each hit stays on one readable line in the
    // MCP text response.
    private static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "[no text]";
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 280 ? collapsed[..277] + "..." : collapsed;
    }
}
