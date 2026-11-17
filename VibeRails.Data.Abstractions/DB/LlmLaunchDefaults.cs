using VibeRails.Services;

namespace VibeRails.DB;

/// <summary>The existing per-provider GlobalCache keys shared by the picker and Board.</summary>
public static class LlmLaunchDefaults
{
    /// <summary>Returns the saved launch-defaults key for a base LLM.</summary>
    public static string CacheKey(LLM llm) => "ui.llm-launch-defaults.v1." + LlmParser.ToWireName(llm).ToLowerInvariant();
}
