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
}
