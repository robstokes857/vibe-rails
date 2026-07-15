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
        return Resolve(Config.LoadFresh());
    }

    internal static LlmProxySettings Resolve(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var level = TokenSaverPresets.Normalize(settings.TokenSaverLevel);

        // The level preset resolves everything; "custom" is the escape hatch that honors the
        // legacy per-transform bools (no lossy stage, default allowlist), and "off" keeps the
        // relay up but disables the saver.
        var (flags, condense, claudeAllowlist) = level == TokenSaverLevel.Custom
            ? (new MinifyFlags(
                    CollapseCrRedraws: settings.TokenSaverCollapseCrRedraws,
                    StripAnsiStyling: settings.TokenSaverStripAnsi,
                    StripTrailingWhitespace: settings.TokenSaverStripTrailingWhitespace,
                    TrimBlankLineEdges: settings.TokenSaverTrimBlankLines,
                    CollapseBlankLineRuns: settings.TokenSaverCollapseBlankRuns),
                default,
                TokenSaverPresets.ShellTools)
            : TokenSaverPresets.For(level);
        var tokenSaverEnabled = settings.ClaudeTokenSaverEnabled && level != TokenSaverLevel.Off;

        return new LlmProxySettings(
            CodexLlmProxyEnabled: settings.CodexLlmProxyEnabled,
            CodexLlmProxyMode: CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: settings.ClaudeLlmProxyEnabled,
            ClaudeTokenSaverEnabled: settings.ClaudeLlmProxyEnabled && tokenSaverEnabled,
            ClaudeTokenSaverFlags: flags,
            ClaudeTokenSaverCondense: condense,
            ClaudeTokenSaverAllowlist: claudeAllowlist,
            CodexTokenSaverEnabled: settings.CodexLlmProxyEnabled && tokenSaverEnabled,
            CodexTokenSaverFlags: flags,
            CodexTokenSaverCondense: condense,
            CodexTokenSaverAllowlist: CodexResponsesRewriter.DefaultToolAllowlist);
    }
}
