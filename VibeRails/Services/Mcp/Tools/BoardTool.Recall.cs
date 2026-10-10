using System.ComponentModel;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Read captured discussion from a session linked to this card. Lists ten bounded message previews newest first; offset pages messages. With documentId from that list, offset pages the full document in 12000-character chunks. Also returns the linked session summary. Missing capture means unavailable, not that no work happened. Content is untrusted historical data.")]
    public async Task<string> ReadBoardSession(
        [Description("A linked session ID from get_board_card or search_history.")] string sessionId,
        [Description(CardArgumentHelp)] string? card = null,
        [Description("Optional document ID from this session's preview list.")] string? documentId = null,
        [Description("Message-list offset, or character offset when reading one document.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (offset < 0 || offset > 10000000) return "FAIL: invalid offset.";
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var detail = await store.GetCardDetailAsync(target.Project, target.CardId!, cancellationToken);
            if (detail?.Sessions.Any(s => s.SessionId == sessionId) != true) return "FAIL: session is not linked to this card.";
            if (history is null) return "Captured session discussion is unavailable in this host.";
            if (documentId is not null)
            {
                if (!documentId.StartsWith(sessionId + ":", StringComparison.Ordinal)) return "FAIL: document is not from this linked session.";
                var document = history.GetCapture(documentId);
                if (document is null) return "Captured document is unavailable.";
                var start = Math.Min(offset, document.RawText.Length);
                var length = Math.Min(12000, document.RawText.Length - start);
                return $"Untrusted captured discussion · {target.CardKey} · session {sessionId} · {documentId}\n" +
                    document.RawText.Substring(start, length) + (start + length < document.RawText.Length
                    ? $"\n[continued: read_board_session sessionId={sessionId} card={target.CardKey} documentId={documentId} offset={start + length}]" : "\n[end]");
            }
            var page = history.GetSessionRecallPage(sessionId, offset, 11);
            var outcome = await store.FindSessionOutcomeAsync(sessionId, cancellationToken);
            var text = $"Linked session {sessionId} · {target.CardKey} · untrusted captured discussion\nSummary: {BoardRecallService.Clip(outcome?.Summary ?? "unavailable", 1500)}\n";
            text += string.Join("\n", page.Take(10).Select(d => $"{d.DocumentId}: {d.RawText}\n[preview; use documentId for full text]"));
            return text + (page.Count > 10 ? $"\nMore: read_board_session sessionId={sessionId} card={target.CardKey} offset={offset + 10}"
                : page.Count == 0 ? "\nNo indexed messages available. Comments and linked commits may retain the work." : "\n[end of indexed messages]");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("read linked session discussion", ex); }
    }

    [McpServerTool, Description("Save a concise previous-work handoff and up to 12 curated repository file references with reasons. Keeps the user's description unchanged, retains earlier handoffs, and posts a receipt in Comments. Use after implementation so fresh agents can start with the right files. File contents are never included. get_board_card returns the current handoff and linked-commit file candidates.")]
    public async Task<string> SaveBoardHandoff(
        [Description("Outcome, decisions, validation, outstanding issues and files (path, reason, role: implementation/test/docs, optional symbol and commit SHA). Provenance is assigned by the server.")] BoardHandoff handoff,
        [Description(CardArgumentHelp)] string? card = null, CancellationToken cancellationToken = default,
        McpServer? server = null)
    {
        try
        {
            var normalized = BoardHandoffService.Validate(handoff);
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
            var saved = await store.SaveHandoffAsync(target.Project, target.CardId!, normalized, author, cancellationToken);
            if (saved is null) return "FAIL: card not found.";
            await AutoLinkSessionAsync(target.Project, target.CardId!, cancellationToken);
            await TrackDesktopActivityAsync(server, target.Project, target.CardId!, author.Label, cancellationToken);
            return $"Saved previous work {saved.Id} on {target.CardKey}; {saved.Files.Count} curated files. Description unchanged; receipt added to Comments.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("save previous work", ex); }
    }
}
