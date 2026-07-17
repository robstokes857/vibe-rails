using TokenSaver.Pipeline;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LlmProxy;

/// <summary>
/// Pins the settings service's half of the token-saver contract: TokenSaverStages is the ONE knob,
/// plus the two switches that can veto it. Legacy tier migration is pinned separately so an
/// upgrade cannot silently reverse an explicit "off" choice.
/// </summary>
public sealed class LlmProxySettingsServiceTests
{
    /// <summary>Both proxies and the master saver switch on, so each test varies one thing.</summary>
    private static Settings ProxyOn(List<string>? stages) => new()
    {
        ClaudeLlmProxyEnabled = true,
        CodexLlmProxyEnabled = true,
        ClaudeTokenSaverEnabled = true,
        TokenSaverStages = stages,
    };

    [Fact]
    public void Resolve_MasterKillSwitchDisablesBothProviderSavers()
    {
        // Named for Claude for legacy reasons; it governs Codex too.
        var settings = ProxyOn(null);
        settings.ClaudeTokenSaverEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings, tabTokenSaverEnabled: true);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.False(resolved.CodexTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_EnablesEachSaverOnlyWithItsProviderProxy()
    {
        var settings = ProxyOn(null);
        settings.ClaudeLlmProxyEnabled = false;

        var resolved = LlmProxySettingsService.Resolve(settings, tabTokenSaverEnabled: true);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_TabGateDisablesBothProviderSaversWithoutChangingTheirPlan()
    {
        var resolved = LlmProxySettingsService.Resolve(
            ProxyOn(null),
            tabTokenSaverEnabled: false);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.False(resolved.CodexTokenSaverEnabled);
        Assert.True(resolved.ResolvedPlan.EnabledIds.SetEquals(CompressionCatalog.DefaultSelection));
    }

    [Fact]
    public void Resolve_NullStages_MeansNeverConfigured_AndTakesTheCatalogDefaults()
    {
        var plan = LlmProxySettingsService.Resolve(ProxyOn(null), tabTokenSaverEnabled: true).ResolvedPlan;

        Assert.True(plan.EnabledIds.SetEquals(CompressionCatalog.DefaultSelection));
    }

    [Fact]
    public void Resolve_EmptyStages_IsARealChoice_AndMustNotBecomeTheDefaults()
    {
        // The trap this test exists for: a `?? []` anywhere on this path silently turns the user's
        // "everything off" back into the defaults. Null and empty are different answers, and empty
        // is the one a human actually chose.
        var plan = LlmProxySettingsService.Resolve(ProxyOn([]), tabTokenSaverEnabled: true).ResolvedPlan;

        Assert.Empty(plan.EnabledIds);
        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.AnthropicAllowlist);
        Assert.Empty(plan.CodexAllowlist);
    }

    [Fact]
    public void Resolve_UnknownIds_AreIgnoredRatherThanThrowing()
    {
        // A settings.json written by a newer build must degrade to "that stage is off here", never
        // to a hard failure on an LLM request.
        var plan = LlmProxySettingsService
            .Resolve(ProxyOn(["not-a-real-stage", CompressionCatalog.AnsiStrip]), tabTokenSaverEnabled: true)
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
        var settings = ProxyOn(null);
        settings.TokenSaverCaptureEnabled = true;

        var resolved = LlmProxySettingsService.Resolve(settings, tabTokenSaverEnabled: true);

        Assert.Same(resolved.TokenSaverPlan, resolved.ResolvedPlan);
        Assert.True(resolved.TokenSaverCaptureEnabled);
    }

    [Fact]
    public void LegacyMigration_PreservesOffAsAnEmptySelection()
    {
        var settings = new Settings { TokenSaverLevel = "off" };

        Assert.Empty(LegacyTokenSaverSettingsMigration.FromLegacy(settings));
    }

    [Fact]
    public void LegacyMigration_PreservesHighTierFeatures()
    {
        var selection = LegacyTokenSaverSettingsMigration.FromLegacy(
            new Settings { TokenSaverLevel = "high" });

        Assert.Contains(CompressionCatalog.CrCollapse, selection);
        Assert.Contains(CompressionCatalog.ScopeShellBackground, selection);
        Assert.Contains(CompressionCatalog.DedupeLines, selection);
        Assert.Contains(CompressionCatalog.TruncateLong, selection);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"TokenSaverStages\":null}", true)]
    [InlineData("{\"tokenSaverStages\":[]}", true)]
    public void LegacyMigration_OnlyRunsWhenStagePropertyIsAbsent(string json, bool expected)
    {
        Assert.Equal(expected, LegacyTokenSaverSettingsMigration.HasStageProperty(json));
    }
}
