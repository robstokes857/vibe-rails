using TokenSaver;
using TokenSaver.Minify;
using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Host-side implementation of the TokenSaver library's settings seam: reads the proxy settings
/// from settings.json (via <see cref="Config"/>) in one fresh snapshot. The interface and the
/// snapshot record live in the library; only this disk-coupled reader stays in the app.
/// </summary>
public sealed class LlmProxySettingsService : ILlmProxySettingsService
{
    public LlmProxySettings GetSettings()
    {
        var settings = Config.LoadFresh();
        return new LlmProxySettings(
            settings.CodexLlmProxyEnabled,
            CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            settings.ClaudeLlmProxyEnabled,
            settings.ClaudeTokenSaverEnabled,
            new MinifyFlags(
                CollapseCrRedraws: settings.TokenSaverCollapseCrRedraws,
                StripAnsiStyling: settings.TokenSaverStripAnsi,
                StripTrailingWhitespace: settings.TokenSaverStripTrailingWhitespace,
                TrimBlankLineEdges: settings.TokenSaverTrimBlankLines,
                CollapseBlankLineRuns: settings.TokenSaverCollapseBlankRuns));
    }
}
