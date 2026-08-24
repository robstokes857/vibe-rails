using TokenSaver;
using TokenSaver.Pipeline;
using VibeRails.Utils;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Host-side implementation of the TokenSaver library's settings seam: reads the proxy settings
/// from settings.json (via <see cref="Config"/>) in one fresh snapshot. The interface and the
/// snapshot record live in the library; only this disk-coupled reader stays in the app.
/// </summary>
public sealed class LlmProxySettingsService(
    ILlmProxySessionState proxySessionState,
    ITokenSaverPauseState tokenSaverPauseState) : ILlmProxySettingsService, ITokenSaverConfiguration
{
    public LlmProxySettings GetSettings()
    {
        return Resolve(
            Config.LoadFresh(),
            proxySessionState.OpenCodeProxyActive,
            tokenSaverPauseState.IsPaused);
    }

    /// <summary>
    /// Answers "is compression switched on for this session" while a pause is in force, which
    /// <see cref="GetSettings"/> deliberately cannot: it reports the effective state, where a pause
    /// and a saver switched off in Settings look identical. The control endpoint needs to tell
    /// those apart so it never promises an agent that its output is about to change when the saver
    /// was never running.
    ///
    /// Answered for <em>this</em> process's provider, not for any of them. The savers are toggled
    /// per provider while a proxy process serves exactly one — so an OR across all three would tell
    /// a Claude tab with its own saver switched off that compression is on merely because Codex's
    /// is, and the agent would then be told a pause took effect that changes nothing.
    /// </summary>
    public bool IsConfiguredOn()
    {
        var configured = Resolve(
            Config.LoadFresh(),
            proxySessionState.OpenCodeProxyActive,
            tokenSaverPaused: false);

        return IsConfiguredOn(configured, proxySessionState.ActiveProvider);
    }

    /// <summary>
    /// The provider selection on its own, split out from <see cref="IsConfiguredOn()"/> so it can be
    /// tested without <see cref="Config.LoadFresh"/> reading the developer's real settings.json.
    /// </summary>
    internal static bool IsConfiguredOn(LlmProxySettings configured, LlmProxyProvider activeProvider) =>
        activeProvider switch
        {
            LlmProxyProvider.Claude => configured.ClaudeTokenSaverEnabled,
            LlmProxyProvider.Codex => configured.CodexTokenSaverEnabled,
            LlmProxyProvider.OpenCode => configured.OpenCodeTokenSaverEnabled,
            // Native Grok minify runs the cli-chat composite (Responses + Chat Completions
            // shapes) against the plan's GrokAllowlist.
            LlmProxyProvider.Grok => configured.GrokTokenSaverEnabled,
            // No proxied launch in this process. Reachable in the root/dashboard backend, which
            // hosts no tab's relay: report "on" if anything is configured on, since the honest
            // answer is that this process is not the one compressing anything.
            _ => configured.ClaudeTokenSaverEnabled
                || configured.CodexTokenSaverEnabled
                || configured.OpenCodeTokenSaverEnabled
        };

    internal static LlmProxySettings Resolve(
        Settings settings,
        bool openCodeProxyActive = false,
        bool tokenSaverPaused = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The plan is the curated catalog set unless a hand-edited TokenSaverStageOverride is
        // present. Null means "no override" and resolves to the catalog defaults; an empty list is
        // a real "run nothing" choice and is honored — hence the nullable round-trip, not `?? []`.
        var plan = CompressionCatalog.Resolve(settings.TokenSaverStageOverride);

        // The saver is on/off per LLM. The Codex/OpenCode toggles are nullable so a pre-split
        // settings.json (which had only the master switch, named for Claude) keeps its old
        // behavior: absent keys inherit the master's value.
        var claudeSaver = settings.ClaudeTokenSaverEnabled;
        var codexSaver = settings.CodexTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled;
        var openCodeSaver = settings.OpenCodeTokenSaverEnabled ?? settings.ClaudeTokenSaverEnabled;
        var grokSaver = settings.GrokTokenSaverEnabled ?? openCodeSaver;
        // Launch injection is a per-session decision. Once an OpenCode terminal has received the
        // local base URL, keep its authenticated relay alive until that terminal exits even if the
        // global launch toggle changes in the meantime. A later terminal still reads the new value.
        var openCodeProxyEnabled = settings.OpenCodeLlmProxyEnabled || openCodeProxyActive;
        var grokProxyEnabled = settings.GrokLlmProxyEnabled || openCodeProxyActive;

        // A pause suppresses only the saver, never the relay: the proxy must keep routing,
        // authenticating and exchange-logging exactly as before, and a paused request is simply
        // forwarded with its bodies untouched. Applying it here rather than in each provider route
        // is what makes one gate cover Claude, Codex, OpenCode and anything added later.
        return new LlmProxySettings(
            CodexLlmProxyEnabled: settings.CodexLlmProxyEnabled,
            CodexLlmProxyMode: CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: settings.ClaudeLlmProxyEnabled,
            OpenCodeLlmProxyEnabled: openCodeProxyEnabled,
            ClaudeTokenSaverEnabled: !tokenSaverPaused && settings.ClaudeLlmProxyEnabled && claudeSaver,
            CodexTokenSaverEnabled: !tokenSaverPaused && settings.CodexLlmProxyEnabled && codexSaver,
            OpenCodeTokenSaverEnabled: !tokenSaverPaused && openCodeProxyEnabled && openCodeSaver,
            TokenSaverPlan: plan,
            TokenSaverCaptureEnabled: settings.TokenSaverCaptureEnabled,
            GrokLlmProxyEnabled: grokProxyEnabled,
            GrokLlmProxyMode: LlmProxyCliChatConfig.NormalizeMode(settings.GrokLlmProxyMode),
            GrokTokenSaverEnabled: !tokenSaverPaused && grokProxyEnabled && grokSaver)
        {
            OpenCodeLlmProxyLaunchEnabled = settings.OpenCodeLlmProxyEnabled,
            GrokLlmProxyLaunchEnabled = settings.GrokLlmProxyEnabled
        };
    }
}
