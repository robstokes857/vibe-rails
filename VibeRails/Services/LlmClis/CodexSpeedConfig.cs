namespace VibeRails.Services.LlmClis;

/// <summary>Canonical Codex configuration keys shared by saved environments and launch overrides.</summary>
internal static class CodexSpeedConfig
{
    public const string ServiceTier = "service_tier";
    public const string Features = "features";
    public const string FastMode = "fast_mode";
    public const string FastModeKey = Features + "." + FastMode;

    public static string[] BuildArguments(string tier) =>
        ["-c", $"{ServiceTier}={tier}", "-c", $"{FastModeKey}=true"];
}
