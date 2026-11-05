using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;

namespace VibeRails.Services;

public sealed partial class LlmPickerPreferenceService
{
    private const string LaunchDefaultsPrefix = "ui.llm-launch-defaults.v1.";

    /// <inheritdoc />
    public async Task<BaseLlmOptions> GetLaunchDefaultsAsync(string cli, CancellationToken cancellationToken = default)
    {
        var llm = ParseDefaultsCli(cli);
        var json = await _repository.GetGlobalCacheValueAsync(DefaultsKey(llm), cancellationToken);
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            return BaseLlmOptionsBuilder.Normalize(llm,
                JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.BaseLlmOptions)) ?? new();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            // A stale or malformed preset never stops a launch or rewrites the stored choice.
            return new();
        }
    }

    /// <inheritdoc />
    public async Task<BaseLlmOptions> SaveLaunchDefaultsAsync(string cli, BaseLlmOptions options, CancellationToken cancellationToken = default)
    {
        var llm = ParseDefaultsCli(cli);
        var normalized = BaseLlmOptionsBuilder.Normalize(llm, options) ?? new();
        // One existing GlobalCache row per CLI: unrelated LLM saves cannot overwrite each other.
        await _repository.SetGlobalCacheValueAsync(DefaultsKey(llm),
            JsonSerializer.Serialize(normalized, AppJsonSerializerContext.Default.BaseLlmOptions), cancellationToken);
        return normalized;
    }

    private static LLM ParseDefaultsCli(string cli)
    {
        var llm = LlmParser.ParseValue(cli);
        if (!Enum.IsDefined(llm) || llm is LLM.NotSet or LLM.Shell)
            throw new ArgumentException("Choose a base LLM to save launch defaults.");
        return llm;
    }

    private static string DefaultsKey(LLM llm) => LaunchDefaultsPrefix + LlmParser.ToWireName(llm).ToLowerInvariant();
}
