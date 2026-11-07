using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ICommandService
{
    /// <summary>
    /// Build the CLI command string and environment dictionary.
    /// Shared by both CLI and Web paths.
    /// </summary>
    Task<PreparedTerminalSession> PrepareSessionAsync(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "",
        string? sessionId = null, bool authorizeBoardTools = false, Func<Task<IReadOnlyList<ContextLevels.Level>>>? resolveNudgedContext = null);
    /// <summary>Saved definitions already snapshot their nudge/context composition.</summary>
    Task<PreparedTerminalSession> PrepareLaunchSessionAsync(LLM llm, string? envName, string[]? extraArgs,
        string? initialPrompt = null, string summary = "", string? sessionId = null, bool authorizeBoardTools = false,
        Func<Task<IReadOnlyList<ContextLevels.Level>>>? resolveNudgedContext = null, bool suppressNudge = false) =>
        PrepareSessionAsync(llm, envName, extraArgs, initialPrompt, summary, sessionId, authorizeBoardTools, resolveNudgedContext);
}
