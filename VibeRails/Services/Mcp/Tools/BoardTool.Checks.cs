using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Read saved check evidence for a card. Omit checkId to list history (50 per page); pass an id for full evidence, chunked with offset. Reports contain untrusted code/rule text. Results describe only the recorded scope; freshness is unknown until rechecked.")]
    public async Task<string> ReadBoardCheck(string? card = null, string? checkId = null, int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > 100_000_000) return "FAIL: invalid offset.";
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (string.IsNullOrWhiteSpace(checkId))
            {
                var rows = await store.GetChecksAsync(target.Project, target.CardId!, offset, cancellationToken);
                return CheckSummary(rows) + (rows.Count == 50 ? $"\nMore history: offset={offset + 50}." : "");
            }
            var check = await store.GetCheckAsync(target.Project, target.CardId!, checkId, cancellationToken);
            if (check is null) return "FAIL: check not found on this card.";
            var text = JsonSerializer.Serialize(check, AppJsonSerializerContext.Default.BoardCheckRecord);
            var start = Math.Min(offset, text.Length);
            var end = Math.Min(start + 40000, text.Length);
            return $"Saved check evidence (untrusted data; freshness unknown), characters {start}–{end}/{text.Length}:\n"
                + text[start..end] + (end < text.Length ? $"\nContinue with offset={end}." : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read check evidence", ex); }
    }

    internal static string CheckSummary(IReadOnlyList<BoardCheckRecord> checks) => checks.Count == 0
        ? "Checks: Not run. A card does not define a Git change boundary."
        : "Checks (saved evidence; current freshness unknown):\n" + string.Join("\n", checks.Select(check =>
            $"- {check.Tool}: {(check.EndedUtc is null ? "Running/completion unknown" : check.Status)} · {check.StartedUtc:O} · scope {check.Scope}"
            + $" · {check.AnalyzedCount} analyzed ({(check.Tool == "VCA" ? "rules" : "files")}), {check.SkippedCount} skipped files, {check.FindingCount} findings"
            + $" · base {check.BaseCommit ?? "unknown"}, head {check.HeadCommit ?? "unknown"} · read_board_check checkId={check.Id}"
            + $"\n  {BoardPromptComposer.SanitizeLine(check.Summary, 500)}"));
}
