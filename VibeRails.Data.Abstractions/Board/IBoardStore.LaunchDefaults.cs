namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>Reads this machine's saved base-LLM defaults; null when none are stored.</summary>
    Task<string?> GetLaunchDefaultsJsonAsync(LLM llm, CancellationToken cancellationToken = default);
}
