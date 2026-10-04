using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

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
    private readonly Func<IUnifiedSearchService> _search;
    private readonly BoardRecallService? _cards;

    public SessionSearchTool(IUnifiedSearchService search, BoardRecallService? cards = null)
        : this(() => search, cards) { }

    public SessionSearchTool(Func<IUnifiedSearchService> search, BoardRecallService? cards = null)
    {
        _search = search;
        _cards = cards;
    }

    /// <summary>Exact card lookup must remain available even if loading the semantic model fails.</summary>
    public static SessionSearchTool Create(IServiceProvider services) => new(
        () => services.GetRequiredService<IUnifiedSearchService>(), services.GetRequiredService<BoardRecallService>());

    [McpServerTool]
    [Description(
        "Recall previous work. When the user names a card, use get_board_card first. " +
        "Explicit keys (e.g. 'what changed on VB-10?') return exact cards before history (short/display IDs are current-project; permanent keys search all local boards), " +
        "including previous work, file references, commits and linked-session summaries; missing keys are explicit. " +
        "Queries without keys also discover cards across all local boards using BGE and keywords, preferring the current repository. Foreign repositories are labeled. " +
        "Search the developer's own captured agent history — past user messages and whole-session " +
        "summaries from previous Claude/Codex/Copilot/Antigravity/OpenCode sessions in the local history corpus. Use this " +
        "to recall what was previously asked, decided, tried, or fixed before redoing work or asking the " +
        "user. Results are ranked by reciprocal-rank fusion across semantic (BGE embedding) and literal " +
        "keyword matches, best first.")]
    public async Task<string> SearchHistory(
        [Description("Natural-language query, e.g. 'how did we fix the websocket reconnect timeout'.")] string query,
        [Description("Maximum history matches (default 10, capped at 50), plus up to five discovered cards or ten exact references.")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "FAIL: query cannot be empty.";
        }

        if (query.Length > 1000) return "FAIL: query is limited to 1000 characters.";
        maxResults = Math.Clamp(maxResults <= 0 ? 10 : maxResults, 1, 50);
        BoardRecallService.Result? cards;
        try { cards = _cards is null ? null : await _cards.SearchAsync(query, maxResults, cancellationToken); }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "[Board] Card recall unavailable");
            cards = new("Card recall unavailable; the following history is not an exact-card result. Use get_board_card for explicit keys.\n", new HashSet<string>(), false);
        }
        UnifiedSearchResponse response;
        try
        {
            response = _search().Search(query, maxResults);
        }
        catch (ArgumentException ex) when (string.IsNullOrEmpty(cards?.Text))
        {
            return $"FAIL: {ex.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException && cards?.Text.Length > 0)
        {
            Serilog.Log.Warning(ex, "[Board] Broader history unavailable during card recall");
            return cards.Text + "Broader captured history is unavailable; card context was recovered.";
        }

        // The fused group is RRF across per-message-semantic, per-session-semantic and lexical —
        // the single best-overall ranking, which is what an agent wants.
        var fused = response.Groups.FirstOrDefault(g => g.Key == "fused");
        var hits = fused?.Hits ?? new List<BertSearchHitResponse>();

        if (hits.Count == 0)
        {
            return (cards?.Text ?? "") + $"No matches for \"{query}\" in the captured session history.";
        }

        var sb = new StringBuilder();
        sb.Append(cards?.Text);
        sb.AppendLine(cards?.Exact == true ? "Broader captured-history fallback (not proof of a card association):" : "Captured session history:");
        sb.AppendLine($"Top {hits.Count} match(es) for \"{query}\" (fused semantic + keyword ranking):");
        sb.AppendLine();

        var rank = 1;
        foreach (var hit in hits.OrderByDescending(hit => cards?.SessionIds.Contains(hit.SessionId) == true))
        {
            var when = hit.TimestampUTC?.ToString("u") ?? "unknown time";
            var kind = hit.Kind == "session-chunk" ? "session summary" : "message";
            var locus = hit.Kind == "session-chunk"
                ? (hit.ChunkIndex is int chunk ? $"chunk {chunk}" : "chunk")
                : (hit.Sequence is int seq ? $"#{seq}" : "msg");

            var source = cards?.SessionIds.Contains(hit.SessionId) == true ? "linked-session history" : "broader history";
            sb.AppendLine($"{rank}. [{source} · {hit.Cli ?? "?"}] {kind} {locus} · {when} · session {hit.SessionId}");
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
