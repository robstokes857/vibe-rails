using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ICommandService
{
    /// <summary>
    /// Build the CLI command string and environment dictionary.
    /// Shared by both CLI and Web paths.
    /// </summary>
    PreparedTerminalSession PrepareSession(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "");
}
