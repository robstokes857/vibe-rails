namespace VibeRails.Services.Board;

/// <summary>
/// The LLM a VibeRails terminal session runs: its CLI wire name and, when it was launched from a
/// saved environment the picker offers, that environment's id (VIBE-185).
/// </summary>
public sealed record BoardSessionLlm(string Cli, int? EnvironmentId);

public partial interface IBoardStore
{
    /// <summary>
    /// What a session is running, so an agent's new card can be assigned to it: the picker key the
    /// Board launched it with, else the terminal's recorded CLI and environment. A Worker or hidden
    /// environment answers with its bare CLI. Null for an unknown session or a plain shell.
    /// </summary>
    Task<BoardSessionLlm?> FindSessionLlmAsync(string sessionId, CancellationToken cancellationToken = default);
}
