using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ICommandService
{
    /// <summary>
    /// Build the CLI command string and environment dictionary.
    /// Shared by both CLI and Web paths. Async because MCP setup consults the global cache
    /// (per-CLI opt-out bookkeeping).
    /// </summary>
    Task<PreparedTerminalSession> PrepareSessionAsync(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "");

    /// <summary>
    /// Records that the one-time opted-out <c>mcp remove</c> for <paramref name="llm"/> has been
    /// issued, so future launches don't re-emit it. Call only AFTER the setup command has actually
    /// been sent to the PTY — building the command is not success (see
    /// <see cref="PreparedTerminalSession.McpRemovalToRecord"/>).
    /// </summary>
    Task RecordMcpRemovalIssuedAsync(LLM llm);
}
