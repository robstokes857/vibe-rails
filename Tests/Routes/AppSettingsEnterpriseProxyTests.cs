using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsEnterpriseProxyTests
{
    [Fact]
    public void Save_RoundTripsClearsAndPreservesOmittedGatewaysWithoutChangingLegacySwitches()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-enterprise-settings-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings
            {
                CodexLlmProxyEnabled = false, ClaudeLlmProxyEnabled = false,
                OpenCodeLlmProxyEnabled = false, GrokLlmProxyEnabled = false
            });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            Assert.True(read.CodexLlmProxyEnabled);
            Assert.True(read.ClaudeLlmProxyEnabled);
            Assert.True(read.OpenCodeLlmProxyEnabled);
            Assert.True(read.GrokLlmProxyEnabled);
            var saved = AppSettingsRoutes.UpdateSettings(read with
            {
                CodexLlmProxyEnabled = true,
                CodexEnterpriseNetworkProxy = " https://genai.example.test/v1/ ",
                ClaudeEnterpriseNetworkProxy = "https://claude.example/v1",
                OpenCodeEnterpriseNetworkProxy = "https://opencode.example/api/v1",
                GrokEnterpriseNetworkProxy = "https://grok.example/v1"
            }, store);
            Assert.Equal("https://genai.example.test/v1", saved.CodexEnterpriseNetworkProxy);
            Assert.Equal("https://claude.example/v1", saved.ClaudeEnterpriseNetworkProxy);
            Assert.Equal("https://opencode.example/api/v1", saved.OpenCodeEnterpriseNetworkProxy);
            Assert.Equal("https://grok.example/v1", saved.GrokEnterpriseNetworkProxy);
            var oldClient = read with
            {
                CodexEnterpriseNetworkProxy = null, ClaudeEnterpriseNetworkProxy = null,
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null
            };
            var preserved = AppSettingsRoutes.UpdateSettings(oldClient, store);
            Assert.Equal(saved.CodexEnterpriseNetworkProxy, preserved.CodexEnterpriseNetworkProxy);
            Assert.Equal(saved.ClaudeEnterpriseNetworkProxy, preserved.ClaudeEnterpriseNetworkProxy);
            Assert.Equal(saved.OpenCodeEnterpriseNetworkProxy, preserved.OpenCodeEnterpriseNetworkProxy);
            Assert.Equal(saved.GrokEnterpriseNetworkProxy, preserved.GrokEnterpriseNetworkProxy);
            Assert.False(store.LoadFresh().CodexLlmProxyEnabled);
            var cleared = AppSettingsRoutes.UpdateSettings(oldClient with { CodexEnterpriseNetworkProxy = " " }, store);
            Assert.Equal("", cleared.CodexEnterpriseNetworkProxy);
            Assert.Equal(saved.ClaudeEnterpriseNetworkProxy, cleared.ClaudeEnterpriseNetworkProxy);

            var before = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexEnterpriseNetworkProxy = "https://valid.example/v1",
                GrokEnterpriseNetworkProxy = "https://user:secret@grok.example/v1"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));
        }
        finally
        {
            ParserConfigs.SetRemoteAccess(remote);
            ParserConfigs.SetApiKey(key);
            ParserConfigs.SetUseVsCodeTheme(theme);
            ParserConfigs.SetMcpEnabled(mcp);
            ParserConfigs.SetRouteThroughVibeRailsAi(relay);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PointingAGatewayAtANewHostNeedsPresenceButClearingOrResavingDoesNot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-enterprise-presence-{Guid.NewGuid():N}");
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            var saved = new Settings { ClaudeEnterpriseNetworkProxy = "https://genai.example.test/anthropic/v1" };
            store.Save(saved);
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            var unchanged = read with
            {
                CodexEnterpriseNetworkProxy = null, ClaudeEnterpriseNetworkProxy = null,
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null
            };
            Assert.False(AppSettingsRoutes.RequiresPresence(unchanged, saved));

            // Same gateway, spelled the way a user might retype it.
            Assert.False(AppSettingsRoutes.RequiresPresence(
                unchanged with { ClaudeEnterpriseNetworkProxy = " https://GENAI.example.test/anthropic/v1/ " }, saved));
            Assert.False(AppSettingsRoutes.RequiresPresence(unchanged with { ClaudeEnterpriseNetworkProxy = "" }, saved));
            Assert.True(AppSettingsRoutes.RequiresPresence(
                unchanged with { ClaudeEnterpriseNetworkProxy = "https://attacker.example/v1" }, saved));
            Assert.True(AppSettingsRoutes.RequiresPresence(
                unchanged with { CodexEnterpriseNetworkProxy = "https://genai.example.test/v1" }, saved));
            Assert.True(AppSettingsRoutes.RequiresPresence(
                unchanged with { GrokEnterpriseNetworkProxy = "not a url" }, saved));

            var handEdited = new Settings { ClaudeEnterpriseNetworkProxy = "https://GENAI.example.test/anthropic/v1/" };
            Assert.False(AppSettingsRoutes.RequiresPresence(
                unchanged with { ClaudeEnterpriseNetworkProxy = handEdited.ClaudeEnterpriseNetworkProxy }, handEdited));

            Assert.True(AppSettingsRoutes.EnterpriseNetworkProxiesAreValid(unchanged));
            Assert.True(AppSettingsRoutes.EnterpriseNetworkProxiesAreValid(unchanged with { OpenCodeEnterpriseNetworkProxy = " " }));
            Assert.False(AppSettingsRoutes.EnterpriseNetworkProxiesAreValid(
                unchanged with { OpenCodeEnterpriseNetworkProxy = "https://user:secret@gateway.example/v1" }));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
