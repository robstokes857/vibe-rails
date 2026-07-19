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
    /// <summary>All proxies and savers explicitly on, so each test varies one thing.</summary>
    private static Settings ProxyOn(List<string>? stageOverride = null) => new()
    {
        ClaudeLlmProxyEnabled = true,
        CodexLlmProxyEnabled = true,
        OpenCodeLlmProxyEnabled = true,
        ClaudeTokenSaverEnabled = true,
        CodexTokenSaverEnabled = true,
        OpenCodeTokenSaverEnabled = true,
        TokenSaverStageOverride = stageOverride,
    };

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
    public void Resolve_EnablesEachSaverOnlyWithItsProviderProxy()
    {
        var settings = ProxyOn();
        settings.ClaudeLlmProxyEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
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
    public void CuratedDefaults_IncludeTheLossyMarkerStages_AndExcludeTuiAndEditRiskIds()
    {
        // Pins the curated-set decision (2026-07-18). Dedupe and truncation are the big safe wins
        // and ship on; the TUI-shaped transforms (cr-collapse, ansi-strip) and the failed-Edit
        // risk scopes (Read, Grep) must never ride in on a defaults change.
        var defaults = CompressionCatalog.DefaultSelection;

        Assert.Contains(CompressionCatalog.ElidePassedTests, defaults);
        Assert.Contains(CompressionCatalog.DedupeLines, defaults);
        Assert.Contains(CompressionCatalog.TruncateLong, defaults);
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
    public void Resolve_UsesOnePlanAndSnapshotsCaptureEnablement()
    {
        var settings = ProxyOn();
        settings.TokenSaverCaptureEnabled = true;

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.Same(resolved.TokenSaverPlan, resolved.ResolvedPlan);
        Assert.True(resolved.TokenSaverCaptureEnabled);
    }

    [Fact]
    public void Resolve_ActiveOpenCodeSessionKeepsProxyAndSaverEnabledAfterLaunchToggleTurnsOff()
    {
        var settings = ProxyOn();
        settings.OpenCodeLlmProxyEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings, openCodeProxyActive: true);

        Assert.True(resolved.OpenCodeLlmProxyEnabled);
        Assert.False(resolved.OpenCodeLlmProxyLaunchEnabled);
        Assert.True(resolved.OpenCodeTokenSaverEnabled);
    }
}
