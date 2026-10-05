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
    /// Who is writing. The launching VibeRails session comes first, because it knows the
    /// environment name. Otherwise the name the MCP client gave in its handshake, which the
    /// protocol requires, so an agent VibeRails did not launch is still recorded as Claude or
    /// Codex rather than "Agent" (VIBE-61). Null means the client named nothing usable: writers
    /// refuse it with <see cref="UnnamedClientHint"/>. Without a server the tool was called
    /// in-process rather than over MCP, and the generic agent label stands.
    /// </summary>
    private async Task<BoardAuthor?> ResolveAuthorAsync(McpServer? server, CancellationToken cancellationToken)
    {
        var sessionId = projects.CurrentSessionId;
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
    /// client keeps its own title or name. Null for a blank or generic ("Agent") name.
    /// </summary>
    internal static BoardAuthor? ClientAuthor(Implementation? client, string? sessionId)
    {
        foreach (var name in new[] { client?.Name, client?.Title })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var word in NonAlphanumeric().Split(name.ToLowerInvariant()))
            {
                if (ClientNameWords.TryGetValue(word, out var llm))
                {
                    var cli = LlmParser.ToWireName(llm);
                    return BoardAuthor.Agent(cli, cli, sessionId);
                }
            }
        }

        var label = CleanClientLabel(client?.Title) ?? CleanClientLabel(client?.Name);
        return label is null ? null : BoardAuthor.Agent(label, null, sessionId);
    }

    private static string? CleanClientLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // The name is client-supplied text that the Board displays: no control characters, one line, bounded.
        var label = string.Join(' ', new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (label.Length > MaxClientLabelLength) label = label[..MaxClientLabelLength].TrimEnd();
        return BoardAuthor.IsGenericAgentLabel(label) ? null : label;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
