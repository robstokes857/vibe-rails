using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    private const string UnnamedClientHint =
        "FAIL: this MCP client did not say which agent it is, so nothing was written. Board writes are recorded under the agent's name, "
        + "taken from the VibeRails session that launched it or from the client's MCP handshake (clientInfo.name, e.g. claude-code or codex-mcp-client).";

    private const int MaxClientLabelLength = 60;

    // Words in a handshake name that identify a CLI VibeRails launches: claude-code, codex-mcp-client, grok-cli.
    private static readonly Dictionary<string, LLM> ClientNameWords = new(StringComparer.Ordinal)
    {
        ["claude"] = LLM.Claude,
        ["codex"] = LLM.Codex,
        ["grok"] = LLM.Grok46,
        ["copilot"] = LLM.Copilot,
        ["opencode"] = LLM.OpenCode,
        ["antigravity"] = LLM.Antigravity,
        ["agy"] = LLM.Antigravity,
    };

    /// <summary>
    /// Who is writing. For terminals the launching VibeRails session comes first, because
    /// it knows the environment name. Desktop clients use their MCP handshake without an
    /// inherited terminal session. Otherwise the name the MCP client gave in its handshake, which the
    /// protocol requires, so an agent VibeRails did not launch is still recorded as Claude or
    /// Codex rather than "Agent" (VIBE-61). Null means the client named nothing usable: writers
    /// refuse it with <see cref="UnnamedClientHint"/>. Without a server the tool was called
    /// in-process rather than over MCP, and the generic agent label stands.
    /// </summary>
    private async Task<BoardAuthor?> ResolveAuthorAsync(McpServer? server, CancellationToken cancellationToken)
    {
        // Desktop clients can inherit correlation metadata without owning that terminal.
        // Attribute their writes to the MCP client, with no terminal recording/replay link.
        var sessionId = desktopActivity?.IsDesktop == true ? null : projects.CurrentSessionId;
        if (sessionId is not null)
        {
            var author = await store.FindSessionAuthorAsync(sessionId, cancellationToken);
            if (author is not null) return author;
        }
        return server is null ? BoardAuthor.Agent("Agent", null, sessionId) : ClientAuthor(server.ClientInfo, sessionId);
    }

    /// <summary>
    /// The author an MCP handshake names. A recognised CLI gets the same label and CLI a
    /// VibeRails session of that CLI would, so the Board shows its name and logo; any other
    /// client keeps its own title or name. Null for a blank, invisible or generic ("Agent") name.
    /// </summary>
    internal static BoardAuthor? ClientAuthor(Implementation? client, string? sessionId)
    {
        var name = CleanClientLabel(client?.Name);
        var title = CleanClientLabel(client?.Title);
        foreach (var text in new[] { name, title })
        {
            if (text is null) continue;
            foreach (var word in NonAlphanumeric().Split(text.ToLowerInvariant()))
            {
                if (ClientNameWords.TryGetValue(word, out var llm))
                {
                    var cli = LlmParser.ToWireName(llm);
                    return BoardAuthor.Agent(cli, cli, sessionId);
                }
            }
        }

        var label = title ?? name;
        return label is null ? null : BoardAuthor.Agent(label, null, sessionId);
    }

    /// <summary>
    /// Client-supplied text the Board displays as an author: format characters (zero-width,
    /// bidi overrides) are dropped, and controls plus every Unicode space or line separator
    /// become single spaces, so the label is one visible, bounded line. Checked against the
    /// generic label after that, so " Agent\u00A0" is still "Agent".
    /// </summary>
    private static string? CleanClientLabel(string? value)
    {
        if (value is null) return null;
        var text = new StringBuilder(value.Length);
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune)) text.Append(' ');
            else text.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }
        var label = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (label.Length > MaxClientLabelLength)
        {
            var cut = char.IsHighSurrogate(label[MaxClientLabelLength - 1]) ? MaxClientLabelLength - 1 : MaxClientLabelLength;
            label = label[..cut].TrimEnd();
        }
        return BoardAuthor.IsGenericAgentLabel(label) ? null : label;
    }

    /// <summary>Who made a new card, as stored beside its agent mark, and anything the caller should hear about it.</summary>
    private readonly record struct CardMaker(string? Name, string? SessionId, string? Note);

    /// <summary>
    /// The name and session recorded on a card an agent creates (VIBE-96). The agent's own
    /// <paramref name="agentName"/> is the most specific ("Claude Opus 5.5"), cleaned like any
    /// client-supplied label; without it the author VibeRails resolved stands, unless that is only
    /// the generic "Agent". The session this process was launched from wins over a supplied id;
    /// a supplied id is kept only when it is a GUID VibeRails has recorded, so a guessed one never
    /// shows as provenance.
    /// </summary>
    private async Task<CardMaker> ResolveCardMakerAsync(BoardAuthor author, string? agentName, string? suppliedSessionId,
        CancellationToken cancellationToken)
    {
        if (author.Kind != BoardAuthor.AgentKind) return default;
        var name = CleanClientLabel(agentName) ?? (BoardAuthor.IsGenericAgentLabel(author.Label) ? null : author.Label);
        if (author.SessionId is not null || string.IsNullOrWhiteSpace(suppliedSessionId))
            return new CardMaker(name, author.SessionId, null);
        if (!Guid.TryParse(suppliedSessionId.Trim(), out var parsed))
            return new CardMaker(name, null, "sessionId was not recorded: it is not a VibeRails session id (a GUID).");
        var sessionId = parsed.ToString("D");
        var session = await store.FindSessionAuthorAsync(sessionId, cancellationToken);
        if (session is null)
            return new CardMaker(name, null, $"sessionId {sessionId} was not recorded: VibeRails has no such session.");
        return new CardMaker(name ?? (BoardAuthor.IsGenericAgentLabel(session.Label) ? null : session.Label), sessionId, null);
    }

    /// <summary>One line naming the agent, session and time behind an agent-made card.</summary>
    internal static string DescribeCardMaker(string? name, string? sessionId, DateTime createdUtc) =>
        "Made by " + (name ?? "an agent")
        + (sessionId is null ? "" : " in session " + sessionId)
        + " at " + createdUtc.ToString("u", CultureInfo.InvariantCulture);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
