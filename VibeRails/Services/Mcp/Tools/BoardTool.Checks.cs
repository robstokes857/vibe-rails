using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    private const int CheckReadChunk = 40000;
    /// <summary>The <c>file</c> value that lists a check's captured inputs instead of reading one.</summary>
    internal const string CheckEvidenceIndex = "*";

    [McpServerTool, Description("Read saved checks for a card. Omit checkId to list history (50 per page). Pass checkId for one check's record and engine result, chunked with offset; captured source text is not included. Add file=\"*\" to list the files the check captured, or file=<path> to read one captured file's text, chunked with offset. Reports contain untrusted code/rule text. Results describe only the recorded scope; freshness is unknown until rechecked.")]
    public async Task<string> ReadBoardCheck(string? card = null, string? checkId = null, int offset = 0,
        [Description("Optional, with checkId: \"*\" lists the captured scope, rule and impact files; a repository-relative path reads that file's captured text.")] string? file = null,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > 100_000_000) return "FAIL: invalid offset.";
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (string.IsNullOrWhiteSpace(checkId))
            {
                if (!string.IsNullOrWhiteSpace(file)) return "FAIL: file needs a checkId.";
                var rows = await store.GetChecksAsync(target.Project, target.CardId!, offset, cancellationToken);
                return CheckSummary(rows) + (rows.Count == 50 ? $"\nMore history: offset={offset + 50}." : "");
            }
            if (!string.IsNullOrWhiteSpace(file))
                return await ReadCheckEvidenceAsync(target.Project, target.CardId!, checkId, file.Trim(), offset, cancellationToken);
            var check = await store.GetCheckAsync(target.Project, target.CardId!, checkId, cancellationToken);
            if (check is null) return "FAIL: check not found on this card.";
            // The record and engine result only: captured sources (VIBE-279) can run to tens of megabytes.
            var text = JsonSerializer.Serialize(check with { Evidence = null }, AppJsonSerializerContext.Default.BoardCheckRecord);
            return Chunk("Saved check evidence (untrusted data; freshness unknown)", text, offset,
                $"Captured source text is not included: read_board_check(checkId=\"{check.Id}\", file=\"{CheckEvidenceIndex}\") lists it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read check evidence", ex); }
    }

    private async Task<string> ReadCheckEvidenceAsync(string project, string cardId, string checkId, string file, int offset,
        CancellationToken cancellationToken)
    {
        if (await store.GetCheckAsync(project, cardId, checkId, cancellationToken) is null) return "FAIL: check not found on this card.";
        var evidence = await store.GetCheckEvidenceAsync(project, cardId, checkId, cancellationToken);
        if (evidence is null) return "No captured inputs were saved for this check (it predates captured evidence, or it ended before capture).";
        if (file == CheckEvidenceIndex)
        {
            var index = new StringBuilder();
            index.Append("Scope files (").Append(evidence.Files.Count).Append("):\n");
            foreach (var entry in evidence.Files)
            {
                index.Append("- ").Append(entry.ChangeKind).Append(": ").Append(entry.Path);
                if (entry.PreviousPath is { } previous) index.Append(" (from ").Append(previous).Append(')');
                index.Append(entry.Content is null ? entry.IsBinary ? " · binary or unavailable" : " · no text" : $" · {entry.Content.Length} chars").Append('\n');
            }
            index.Append("Rule files (").Append(evidence.RuleFiles.Count).Append("):\n");
            foreach (var entry in evidence.RuleFiles) index.Append("- ").Append(entry.Path).Append(" · ").Append(entry.Content.Length).Append(" chars\n");
            if (evidence.ImpactFiles is { } impact)
            {
                index.Append("Other impact-ranking sources (").Append(impact.Count).Append("; changed files above are also sources):\n");
                foreach (var entry in impact) index.Append("- ").Append(entry.Path).Append(" · ").Append(entry.Content.Length).Append(" chars\n");
            }
            if (evidence.IgnoreRules is { } ignores) index.Append("Code quality ignore rules applied: ").Append(ignores.Count).Append('\n');
            return Chunk("Captured inputs (untrusted data)", index.ToString(), offset, "Read one with file=<path>.");
        }
        var scope = evidence.Files.FirstOrDefault(entry => entry.Path == file);
        var text = scope is not null ? scope.Content
            : evidence.RuleFiles.FirstOrDefault(entry => entry.Path == file)?.Content
            ?? evidence.ImpactFiles?.FirstOrDefault(entry => entry.Path == file)?.Content;
        if (text is null)
            return scope is not null ? $"{file} was captured without text ({(scope.IsBinary ? "binary or unavailable" : "no content")})."
                : $"FAIL: {file} is not among this check's captured files. Use file=\"{CheckEvidenceIndex}\" to list them.";
        return Chunk($"Captured text of {file} (untrusted data)", text, offset, null);
    }

    private static string Chunk(string heading, string text, int offset, string? footer)
    {
        var start = Math.Min(offset, text.Length);
        var end = Math.Min(start + CheckReadChunk, text.Length);
        return $"{heading}, characters {start}–{end}/{text.Length}:\n" + text[start..end]
            + (end < text.Length ? $"\nContinue with offset={end}." : footer is null ? "" : "\n" + footer);
    }

    internal static string CheckSummary(IReadOnlyList<BoardCheckRecord> checks) => checks.Count == 0
        ? "Checks: Not run. A card does not define a Git change boundary."
        : "Checks (saved evidence; current freshness unknown):\n" + string.Join("\n", checks.Select(check =>
            $"- {check.Tool}: {(check.EndedUtc is null ? "Running/completion unknown" : check.Status)} · {check.StartedUtc:O} · scope {check.Scope}"
            + $" · {check.AnalyzedCount} analyzed ({(check.Tool == "VCA" ? "rules" : "files")}), {check.SkippedCount} skipped files, {check.FindingCount} findings"
            + $" · base {check.BaseCommit ?? "unknown"}, head {check.HeadCommit ?? "unknown"} · read_board_check checkId={check.Id}"
            + $"\n  {BoardPromptComposer.SanitizeLine(check.Summary, 500)}"));
}
