namespace VibeRails.Services.LlmClis;

// Keep in sync with llm-model-catalog.js and vibe-books/custom_envs/CLI_OPTIONS.md.
// Verified against Codex's model metadata and speed guide, 2026-10-08.
internal static class CodexModelCapabilities
{
    public static bool SupportsSpeed(string? model, string speed) => (model?.Trim().ToLowerInvariant(), speed) switch
    {
        ("gpt-6-astra", "fast" or "ultrafast") => true,
        ("gpt-6.1-sol" or "gpt-6-sol" or "gpt-6-luna" or "gpt-5.6-sol"
            or "gpt-5.6-terra" or "gpt-5.6-luna" or "gpt-5.5", "fast") => true,
        _ => false
    };
}
