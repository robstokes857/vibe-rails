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
        return Resolve(Config.LoadFresh(), tokenSaverPauseState.IsPaused,
            Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
            Environment.GetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable),
            Environment.GetEnvironmentVariable(LlmProxyUpstream.CodexVariable),
            Environment.GetEnvironmentVariable(LlmProxyUpstream.ClaudeVariable));
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
        var configured = Resolve(Config.LoadFresh(), tokenSaverPaused: false);

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
        bool tokenSaverPaused = false,
        string? inheritedCodexBaseUrl = null,
        string? inheritedClaudeBaseUrl = null,
        string? capturedCodexUpstream = null,
        string? capturedClaudeUpstream = null)
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
        // Every supported launch uses the local relay. Legacy proxy switches remain on disk for
        // older versions, but cannot disable routing or a provider's independently selected saver.

        // A pause suppresses only the saver, never the relay: the proxy must keep routing,
        // authenticating and exchange-logging exactly as before, and a paused request is simply
        // forwarded with its bodies untouched. Applying it here rather than in each provider route
        // is what makes one gate cover Claude, Codex, OpenCode and anything added later.
        return new LlmProxySettings(
            CodexLlmProxyEnabled: true,
            CodexLlmProxyMode: !string.IsNullOrWhiteSpace(settings.CodexEnterpriseNetworkProxy)
                && !string.IsNullOrWhiteSpace(settings.CodexGatewayBearerToken)
                ? CodexLlmProxySettings.ModeApi : CodexLlmProxySettings.NormalizeMode(settings.CodexLlmProxyMode),
            ClaudeLlmProxyEnabled: true,
            OpenCodeLlmProxyEnabled: true,
            ClaudeTokenSaverEnabled: !tokenSaverPaused && claudeSaver,
            CodexTokenSaverEnabled: !tokenSaverPaused && codexSaver,
            OpenCodeTokenSaverEnabled: !tokenSaverPaused && openCodeSaver,
            TokenSaverPlan: plan,
            GrokLlmProxyEnabled: true,
            GrokLlmProxyMode: LlmProxyCliChatConfig.NormalizeMode(settings.GrokLlmProxyMode),
            GrokTokenSaverEnabled: !tokenSaverPaused && grokSaver)
        {
            OpenCodeLlmProxyLaunchEnabled = true,
            GrokLlmProxyLaunchEnabled = true,
            CodexEnterpriseNetworkProxy = ResolveGateway(settings.CodexEnterpriseNetworkProxy, inheritedCodexBaseUrl, capturedCodexUpstream),
            ClaudeEnterpriseNetworkProxy = ResolveGateway(settings.ClaudeEnterpriseNetworkProxy, inheritedClaudeBaseUrl, capturedClaudeUpstream),
            ClaudeGatewaySupportsToolSearch = settings.ClaudeGatewaySupportsToolSearch,
            CodexGatewayBearerToken = string.IsNullOrWhiteSpace(settings.CodexEnterpriseNetworkProxy) ? "" : settings.CodexGatewayBearerToken,
            ClaudeGatewayBearerToken = string.IsNullOrWhiteSpace(settings.ClaudeEnterpriseNetworkProxy) ? "" : settings.ClaudeGatewayBearerToken,
            CodexGatewayAuthMode = settings.CodexGatewayAuthMode,
            CodexGatewayApiKeyHeader = settings.CodexGatewayApiKeyHeader,
            ClaudeGatewayAuthMode = settings.ClaudeGatewayAuthMode,
            ClaudeGatewayApiKeyHeader = settings.ClaudeGatewayApiKeyHeader,
            OpenCodeEnterpriseNetworkProxy = settings.OpenCodeEnterpriseNetworkProxy,
            GrokEnterpriseNetworkProxy = settings.GrokEnterpriseNetworkProxy
        };
    }

    /// <summary>Keep the destination behind an inherited relay, or fail closed when it is unknown.</summary>
    internal static string ResolveGateway(string? saved, string? inherited, string? captured = null) =>
        LlmProxyUpstream.Resolve(saved, inherited, captured);
}
