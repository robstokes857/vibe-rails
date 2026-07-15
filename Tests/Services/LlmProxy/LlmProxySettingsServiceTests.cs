using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxySettingsServiceTests
{
    [Fact]
    public void Resolve_MasterKillSwitchDisablesBothProviderSavers()
    {
        var settings = new Settings
        {
            ClaudeLlmProxyEnabled = true,
            CodexLlmProxyEnabled = true,
            ClaudeTokenSaverEnabled = false,
            TokenSaverLevel = "high"
        };

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.False(resolved.CodexTokenSaverEnabled);
    }

    [Fact]
    public void Resolve_EnablesEachSaverOnlyWithItsProviderProxy()
    {
        var settings = new Settings
        {
            ClaudeLlmProxyEnabled = false,
            CodexLlmProxyEnabled = true,
            ClaudeTokenSaverEnabled = true,
            TokenSaverLevel = "safe"
        };

        var resolved = LlmProxySettingsService.Resolve(settings);

        Assert.False(resolved.ClaudeTokenSaverEnabled);
        Assert.True(resolved.CodexTokenSaverEnabled);
    }
}
