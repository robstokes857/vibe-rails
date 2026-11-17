using VibeRails.DB;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<string?> GetLaunchDefaultsJsonAsync(LLM llm, CancellationToken cancellationToken = default)
    {
        // These are the picker's existing machine preferences, not Board-owned state.
        // Read without constructing Repository or running state.db migrations in the MCP child.
        await using var state = await OpenStateAsync(cancellationToken);
        if (!await _stateFeatures.HasTableAsync(state, "GlobalCache", cancellationToken)) return null;
        return await ScalarStringAsync(state, null, SqlStrings.SelectGlobalCacheByKey,
            ("$key", LlmLaunchDefaults.CacheKey(llm)), cancellationToken);
    }
}
