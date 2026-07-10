using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

public interface ILlmProxySettingsService
{
    /// <summary>
    /// Reads all proxy settings in a single fresh snapshot. Callers that need more than one field
    /// (a session launch reads three; the proxy route reads enabled+mode) must use one snapshot so
    /// they can't observe a torn combination across a concurrent settings save, and so a launch
    /// hits the disk once instead of once per property.
    /// </summary>
    LlmProxySettings GetSettings();
}

/// <summary>Immutable snapshot of the LLM-proxy settings, with the mode already normalized.</summary>
public sealed record LlmProxySettings(
    bool CodexLlmProxyEnabled,
    string CodexLlmProxyMode,
    bool ClaudeLlmProxyEnabled);

public sealed class LlmProxySettingsService : ILlmProxySettingsService
{
    public LlmProxySettings GetSettings()
    {
        var settings = Config.LoadFresh();
        return new LlmProxySettings(
            settings.CodexLlmProxyEnabled,
            CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            settings.ClaudeLlmProxyEnabled);
    }
}

public static class CodexLlmProxySettings
{
    public const string ModeSubscription = "subscription";
    public const string ModeApi = "api";

    public static string NormalizeMode(string? mode)
    {
        var trimmed = (mode ?? string.Empty).Trim();
        return trimmed.Equals(ModeApi, StringComparison.OrdinalIgnoreCase)
            ? ModeApi
            : ModeSubscription;
    }
}
