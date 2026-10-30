using TokenSaver.Pipeline;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LlmProxy;

/// <summary>
/// Pins the settings service's half of the token-saver contract: on/off per LLM (with pre-split
/// settings files inheriting the legacy master switch), the curated catalog defaults as the plan,
/// and the hand-edit TokenSaverStageOverride escape hatch.
/// </summary>
public sealed class LlmProxySettingsServiceTests
{
    [Fact]
    public void InheritedGatewaysRetainDestinationAndCompressionWithoutReusingSavedTokens()
    {
        var settings = ProxyOn();
        settings.CodexGatewayBearerToken = settings.ClaudeGatewayBearerToken = "old-secret";
        var resolved = LlmProxySettingsService.Resolve(settings, inheritedCodexBaseUrl: "https://corp.example/v1",
            inheritedClaudeBaseUrl: "https://corp.example/anthropic");
        Assert.Equal("https://corp.example/v1", resolved.CodexEnterpriseNetworkProxy);
        Assert.Equal("https://corp.example/anthropic", resolved.ClaudeEnterpriseNetworkProxy);
        Assert.True(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.ClaudeTokenSaverEnabled);
        Assert.Empty(resolved.CodexGatewayBearerToken);
        Assert.Empty(resolved.ClaudeGatewayBearerToken);

        settings.CodexEnterpriseNetworkProxy = "https://saved.example/v1";
        var saved = LlmProxySettingsService.Resolve(settings, inheritedCodexBaseUrl: "https://corp.example/v1");
        Assert.Equal(settings.CodexEnterpriseNetworkProxy, saved.CodexEnterpriseNetworkProxy);
        Assert.Equal("old-secret", saved.CodexGatewayBearerToken);
        Assert.Equal("api", saved.CodexLlmProxyMode);
    }

    [Fact]
    public void ToolSearchSwitchDefaultsOffAndReachesTheLaunchSnapshot()
    {
        var settings = ProxyOn();
        Assert.False(LlmProxySettingsService.Resolve(settings).ClaudeGatewaySupportsToolSearch);
        settings.ClaudeGatewaySupportsToolSearch = true;
        // Applies to an inherited destination too: it describes the route, not the saved credential.
        Assert.True(LlmProxySettingsService.Resolve(settings, inheritedClaudeBaseUrl: "https://corp.example/anthropic")
            .ClaudeGatewaySupportsToolSearch);
    }

    [Theory]
    [InlineData("http://localhost:456/llm/anthropic", null, "")]
    [InlineData("http://127.0.0.1:456/llm/openai/v1", null, "")]
    [InlineData("http://localhost:456/llm/anthropic", global::TokenSaver.LlmProxyUpstream.Unavailable, global::TokenSaver.LlmProxyUpstream.Unavailable)]
    [InlineData("https://user:secret@corp.example/v1", null, "https://user:secret@corp.example/v1")]
    public void InheritedRelayDoesNotLoopAndInvalidDestinationIsNotSilentlyDropped(string inherited, string? captured, string expected)
    {
        // A relay URL with no metadata at all comes from a release before the capture existed,
        // whose relay forwarded to the provider default (see LlmProxyUpstream.Resolve).
        Assert.Equal(expected, LlmProxySettingsService.ResolveGateway("", inherited, captured));
    }

    [Fact]
    public void CapturedUpstreamIsUsedOnlyBehindARelayAndNeverGrantsASavedCredential()
    {
        var settings = ProxyOn();
        settings.ClaudeGatewayBearerToken = "dormant-secret";
        var nested = LlmProxySettingsService.Resolve(settings,
            inheritedClaudeBaseUrl: "http://localhost:456/llm/anthropic", capturedClaudeUpstream: "https://corporate.example/anthropic");
        Assert.Equal("https://corporate.example/anthropic", nested.ClaudeEnterpriseNetworkProxy);
        Assert.Empty(nested.ClaudeGatewayBearerToken);
        Assert.Equal("https://new.example/v1", LlmProxySettingsService.ResolveGateway("", "https://new.example/v1", "https://old.example/v1"));
        Assert.Equal("https://saved.example/v1", LlmProxySettingsService.ResolveGateway("https://saved.example/v1", "http://localhost:456/llm/anthropic", null));
    }

    /// <summary>All proxies and savers explicitly on, so each test varies one thing.</summary>
    private static Settings ProxyOn(List<string>? stageOverride = null) => new()
    {
        ClaudeLlmProxyEnabled = true,
        CodexLlmProxyEnabled = true,
        OpenCodeLlmProxyEnabled = true,
        GrokLlmProxyEnabled = true,
        ClaudeTokenSaverEnabled = true,
        CodexTokenSaverEnabled = true,
        OpenCodeTokenSaverEnabled = true,
        GrokTokenSaverEnabled = true,
        TokenSaverStageOverride = stageOverride,
    };

    [Fact]
    public void Resolve_LegacyProxySwitchDoesNotDisableOpenCodeSaver()
    {
        var settings = new Settings
        {
            GrokLlmProxyEnabled = true,
            OpenCodeLlmProxyEnabled = false,
            ClaudeTokenSaverEnabled = true,
            OpenCodeTokenSaverEnabled = true
        };

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.True(resolved.GrokTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_EachProviderSaverTogglesIndependently()
    {
        // The 2026-07-18 contract: on/off per LLM, no master switch. Turning one saver off must
        // not leak into its siblings.
        var settings = ProxyOn();
        settings.CodexTokenSaverEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.True(resolved.ClaudeTokenSaverEnabled);
        Assert.False(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_ClaudeToggleOff_NoLongerActsAsAMasterSwitchWhenSiblingsAreExplicit()
    {
        var settings = ProxyOn();
        settings.ClaudeTokenSaverEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_PreSplitFile_InheritsTheLegacyMasterSwitchForAbsentProviderKeys(bool master)
    {
        // A settings.json written before the per-LLM split has only ClaudeTokenSaverEnabled, which
        // governed every provider. Absent (null) Codex/OpenCode keys must keep that behavior so an
        // upgrade cannot silently reverse an explicit "off" choice — the exact regression the old
        // migration tests existed to prevent.
        var settings = ProxyOn();
        settings.ClaudeTokenSaverEnabled = master;
        settings.CodexTokenSaverEnabled = null;
        settings.OpenCodeTokenSaverEnabled = null;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.Equal(master, resolved.ClaudeTokenSaverEnabled);
        Assert.Equal(master, resolved.CodexTokenSaverEnabled);
        Assert.Equal(master, resolved.OpenCodeTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_LegacyProxySwitchDoesNotDisableClaudeSaver()
    {
        var settings = ProxyOn();
        settings.ClaudeLlmProxyEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.True(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_NoOverride_RunsTheCuratedCatalogDefaults()
    {
        var plan = LlmProxySettingsService.Resolve(ProxyOn()).ResolvedPlan;

        Assert.True(plan.EnabledIds.SetEquals(CompressionCatalog.DefaultSelection));
    }

    [Fact]
    public void CuratedDefaults_IncludeTheSafeLossyStages_AndExcludeTruncationTuiAndEditRiskIds()
    {
        // Pins the curated-set decision (2026-07-18, narrowed 2026-10-13 by VIBE-154). Passing-test
        // elision and dedupe drop nothing the agent needs and ship on. truncate-long ships off:
        // agents re-fetched the elided middle often enough to erase its saving on Claude. The
        // TUI-shaped transforms (cr-collapse, ansi-strip) and the failed-Edit risk scopes (Read,
        // Grep) must never ride in on a defaults change either.
        var defaults = CompressionCatalog.DefaultSelection;

        Assert.Contains(CompressionCatalog.ElidePassedTests, defaults);
        Assert.Contains(CompressionCatalog.DedupeLines, defaults);
        Assert.DoesNotContain(CompressionCatalog.TruncateLong, defaults);
        Assert.Contains(CompressionCatalog.ScopeShell, defaults);
        Assert.Contains(CompressionCatalog.ScopeShellBackground, defaults);
        Assert.DoesNotContain(CompressionCatalog.CrCollapse, defaults);
        Assert.DoesNotContain(CompressionCatalog.AnsiStrip, defaults);
        Assert.DoesNotContain(CompressionCatalog.ScopeRead, defaults);
        Assert.DoesNotContain(CompressionCatalog.ScopeGrep, defaults);
    }

    [Fact]
    public void Resolve_EmptyOverride_IsARealChoice_AndMustNotBecomeTheDefaults()
    {
        // The trap this test exists for: a `?? []` anywhere on this path silently turns a
        // hand-edited "run nothing" override back into the defaults. Null and empty are different
        // answers, and empty is the one a human actually chose.
        var plan = LlmProxySettingsService.Resolve(ProxyOn([])).ResolvedPlan;

        Assert.Empty(plan.EnabledIds);
        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.AnthropicAllowlist);
        Assert.Empty(plan.CodexAllowlist);
        Assert.Empty(plan.ZaiAllowlist);
    }

    [Fact]
    public void Resolve_UnknownOverrideIds_AreIgnoredRatherThanThrowing()
    {
        // A settings.json written by a newer build must degrade to "that stage is off here", never
        // to a hard failure on an LLM request.
        var plan = LlmProxySettingsService
            .Resolve(ProxyOn(["not-a-real-stage", CompressionCatalog.AnsiStrip]))
            .ResolvedPlan;

        Assert.True(plan.Flags.StripAnsiStyling);
        Assert.False(plan.Flags.CollapseCrRedraws);
        Assert.False(plan.Flags.StripTrailingWhitespace);
        Assert.True(plan.Condense.IsNoOp);
        Assert.Empty(plan.Shapes);
        Assert.Empty(plan.AnthropicAllowlist);
    }

    [Fact]
    public void Resolve_UsesOnePlan()
    {
        var resolved = LlmProxySettingsService.Resolve(ProxyOn());

        Assert.Same(resolved.TokenSaverPlan, resolved.ResolvedPlan);
    }

    [Fact]
    public void Resolve_Paused_SuppressesEverySaverWithoutTouchingTheRelay()
    {
        // The pause is applied at this one gate rather than in each provider route, so a new
        // provider inherits it by construction. What must NOT change is the relay: turning the
        // proxy off here would 404 the CLI mid-conversation instead of quietly forwarding
        // uncompressed bodies.
        var resolved = LlmProxySettingsService.Resolve(ProxyOn(), tokenSaverPaused: true);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.False(resolved.CodexTokenSaverEnabled);
        Assert.False(resolved.OpenCodeTokenSaverEnabled);

        Assert.True(resolved.ClaudeLlmProxyEnabled);
        Assert.True(resolved.CodexLlmProxyEnabled);
        Assert.True(resolved.OpenCodeLlmProxyEnabled);
    }

    [Fact]
    public void Resolve_Paused_LeavesThePlanIntactSoResumingRestoresTheSameStages()
    {
        // The pause must not be expressed by emptying the plan. The plan is what the capture view
        // and the preview endpoint report as "the config we ran"; a pause that rewrote it would
        // make a paused request indistinguishable from a saver configured to do nothing.
        var resolved = LlmProxySettingsService.Resolve(ProxyOn(), tokenSaverPaused: true);

        Assert.True(resolved.ResolvedPlan.EnabledIds.SetEquals(CompressionCatalog.DefaultSelection));
    }

    [Fact]
    public void Resolve_NotPaused_IsTheDefaultAndChangesNothing()
    {
        var resolved = LlmProxySettingsService.Resolve(ProxyOn(), tokenSaverPaused: false);

        Assert.True(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }

    [Fact]
    public void IsConfiguredOn_ReadsOnlyTheProviderThisProcessProxies()
    {
        // The bug this pins: a proxy child serves exactly one CLI, but the answer used to be an OR
        // across all three savers. A Claude tab whose own saver is off would report compression on
        // merely because Codex's was — so pause_token_saver told the agent it had paused something
        // that was never running, and the meter raised a badge for a tab nothing was compressing.
        var settings = ProxyOn();
        settings.ClaudeTokenSaverEnabled = false;
        var configured = LlmProxySettingsService.Resolve(settings);

        Assert.False(LlmProxySettingsService.IsConfiguredOn(configured, LlmProxyProvider.Claude));
        Assert.True(LlmProxySettingsService.IsConfiguredOn(configured, LlmProxyProvider.Codex));
    }

    [Theory]
    [InlineData(LlmProxyProvider.Claude)]
    [InlineData(LlmProxyProvider.Codex)]
    [InlineData(LlmProxyProvider.OpenCode)]
    [InlineData(LlmProxyProvider.Grok)]
    public void IsConfiguredOn_IsFalseWhenOnlyThisProvidersSaverIsOff(LlmProxyProvider provider)
    {
        var settings = ProxyOn();
        switch (provider)
        {
            case LlmProxyProvider.Claude: settings.ClaudeTokenSaverEnabled = false; break;
            case LlmProxyProvider.Codex: settings.CodexTokenSaverEnabled = false; break;
            case LlmProxyProvider.Grok: settings.GrokTokenSaverEnabled = false; break;
            default: settings.OpenCodeTokenSaverEnabled = false; break;
        }

        var configured = LlmProxySettingsService.Resolve(settings);

        Assert.False(LlmProxySettingsService.IsConfiguredOn(configured, provider));
    }

    [Theory]
    [InlineData(LlmProxyProvider.Claude)]
    [InlineData(LlmProxyProvider.Codex)]
    [InlineData(LlmProxyProvider.OpenCode)]
    [InlineData(LlmProxyProvider.Grok)]
    public void IsConfiguredOn_IsTrueWhenThisProvidersSaverIsTheOnlyOneOn(LlmProxyProvider provider)
    {
        var settings = ProxyOn();
        settings.ClaudeTokenSaverEnabled = provider == LlmProxyProvider.Claude;
        settings.CodexTokenSaverEnabled = provider == LlmProxyProvider.Codex;
        settings.OpenCodeTokenSaverEnabled = provider == LlmProxyProvider.OpenCode;
        settings.GrokTokenSaverEnabled = provider == LlmProxyProvider.Grok;

        var configured = LlmProxySettingsService.Resolve(settings);

        Assert.True(LlmProxySettingsService.IsConfiguredOn(configured, provider));
    }

    [Fact]
    public void IsConfiguredOn_WithNoProxiedLaunch_FallsBackToAnySaverBeingOn()
    {
        // The root/dashboard backend hosts no tab's relay, so there is no "this provider" to ask
        // about. Reporting off would claim compression is switched off app-wide, which is a
        // different and wronger answer than "not here".
        var settings = ProxyOn();
        settings.ClaudeTokenSaverEnabled = false;
        settings.CodexTokenSaverEnabled = false;

        var configured = LlmProxySettingsService.Resolve(settings);

        Assert.True(LlmProxySettingsService.IsConfiguredOn(configured, LlmProxyProvider.None));

        settings.OpenCodeTokenSaverEnabled = false;
        var allOff = LlmProxySettingsService.Resolve(settings);

        Assert.False(LlmProxySettingsService.IsConfiguredOn(allOff, LlmProxyProvider.None));
    }

    [Fact]
    public void IsConfiguredOn_IsBlindToAPause_SoAPausedSaverStillCountsAsConfiguredOn()
    {
        // The control endpoint has to tell "paused" apart from "switched off in Settings". The
        // effective snapshot cannot: a pause reports every saver off. So the question is asked of a
        // pause-blind resolve, and re-pausing inside a live window must still be allowed.
        var configured = LlmProxySettingsService.Resolve(ProxyOn(), tokenSaverPaused: false);

        Assert.True(LlmProxySettingsService.IsConfiguredOn(configured, LlmProxyProvider.Claude));
    }

    [Fact]
    public void Resolve_LegacySwitchesCannotDisableCurrentOrFutureRouting()
    {
        var settings = ProxyOn();
        settings.ClaudeLlmProxyEnabled = false;
        settings.CodexLlmProxyEnabled = false;
        settings.OpenCodeLlmProxyEnabled = false;
        settings.GrokLlmProxyEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.True(resolved.ClaudeLlmProxyEnabled);
        Assert.True(resolved.CodexLlmProxyEnabled);
        Assert.True(resolved.OpenCodeLlmProxyEnabled);
        Assert.True(resolved.GrokLlmProxyEnabled);
        Assert.True(resolved.OpenCodeLlmProxyLaunchEnabled);
        Assert.True(resolved.GrokLlmProxyLaunchEnabled);
        Assert.True(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
        Assert.True(resolved.GrokTokenSaverEnabled);
    }
}
