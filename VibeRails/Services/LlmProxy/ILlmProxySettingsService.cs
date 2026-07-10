using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

public interface ILlmProxySettingsService
{
    bool CodexLlmProxyEnabled { get; }
    string CodexLlmProxyMode { get; }
    bool ClaudeLlmProxyEnabled { get; }
}

public sealed class LlmProxySettingsService : ILlmProxySettingsService
{
    public bool CodexLlmProxyEnabled => Config.LoadFresh().CodexLlmProxyEnabled;
    public string CodexLlmProxyMode => CodexLlmProxySettings.NormalizeMode(Config.LoadFresh().CodexLlmProxyMode);
    public bool ClaudeLlmProxyEnabled => Config.LoadFresh().ClaudeLlmProxyEnabled;
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
