using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.LlmProxy;
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
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null,
                CodexGatewayAuthMode = null, CodexGatewayApiKeyHeader = null,
                ClaudeGatewayAuthMode = null, ClaudeGatewayApiKeyHeader = null
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

            var credentialResponse = AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexEnterpriseNetworkProxy = "https://genai.example.test/v1",
                CodexGatewayBearerToken = "Bearer codex-secret",
                ClaudeGatewayBearerToken = "claude-secret"
            }, store);
            Assert.True(credentialResponse.HasCodexGatewayBearerToken);
            Assert.True(credentialResponse.HasClaudeGatewayBearerToken);
            Assert.Null(credentialResponse.CodexGatewayBearerToken);
            Assert.Null(credentialResponse.ClaudeGatewayBearerToken);
            Assert.DoesNotContain("codex-secret", System.Text.Json.JsonSerializer.Serialize(credentialResponse));
            Assert.Equal("codex-secret", store.LoadFresh().CodexGatewayBearerToken);
            // API auth is derived while the credential exists; the stored choice is left alone.
            Assert.Equal("subscription", store.LoadFresh().CodexLlmProxyMode);
            AppSettingsRoutes.UpdateSettings(oldClient with { ClaudeGatewayBearerToken = "" }, store);
            Assert.Equal("claude-secret", store.LoadFresh().ClaudeGatewayBearerToken);
            AppSettingsRoutes.UpdateSettings(oldClient with { ClearCodexGatewayBearerToken = true }, store);
            Assert.Empty(store.LoadFresh().CodexGatewayBearerToken);
            Assert.Equal("claude-secret", store.LoadFresh().ClaudeGatewayBearerToken);

            var apiKeyResponse = AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexGatewayAuthMode = "api-key", CodexGatewayApiKeyHeader = " X-Corporate-Key ",
                CodexGatewayBearerToken = "raw-corporate-key"
            }, store);
            Assert.Equal("api-key", apiKeyResponse.CodexGatewayAuthMode);
            Assert.Equal("X-Corporate-Key", apiKeyResponse.CodexGatewayApiKeyHeader);
            Assert.Null(apiKeyResponse.CodexGatewayBearerToken);
            Assert.Equal("raw-corporate-key", store.LoadFresh().CodexGatewayBearerToken);
            AppSettingsRoutes.UpdateSettings(oldClient, store);
            Assert.Equal("api-key", store.LoadFresh().CodexGatewayAuthMode);
            Assert.Equal("X-Corporate-Key", store.LoadFresh().CodexGatewayApiKeyHeader);

            var before = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexGatewayApiKeyHeader = "Host", CodexGatewayBearerToken = "must-not-save"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexGatewayBearerToken = "secret\r\nInjected: header"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(oldClient with
            {
                CodexEnterpriseNetworkProxy = "", CodexGatewayBearerToken = "secret"
            }, store));
            Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "settings.json")));
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
    public void ASavedCredentialNeverFollowsItsGatewayToANewOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-enterprise-origin-{Guid.NewGuid():N}");
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
                CodexEnterpriseNetworkProxy = "https://genai.corp.example/v1", CodexGatewayBearerToken = "corp-token",
                ClaudeEnterpriseNetworkProxy = "https://genai.corp.example/anthropic", ClaudeGatewayBearerToken = "corp-key"
            });
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            var client = read with
            {
                CodexEnterpriseNetworkProxy = null, ClaudeEnterpriseNetworkProxy = null,
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null
            };

            // A new path or a retyped spelling on the same origin keeps the credential.
            AppSettingsRoutes.UpdateSettings(client with { CodexEnterpriseNetworkProxy = "https://GENAI.corp.example:443/openai/v1/" }, store);
            Assert.Equal("corp-token", store.LoadFresh().CodexGatewayBearerToken);

            // The "OpenAI API key" choice saved without typing a key: the corporate token must
            // not be sent to api.openai.com. Only that provider's credential is affected.
            var moved = AppSettingsRoutes.UpdateSettings(client with { CodexEnterpriseNetworkProxy = "https://api.openai.com/v1" }, store);
            Assert.False(moved.HasCodexGatewayBearerToken);
            Assert.Empty(store.LoadFresh().CodexGatewayBearerToken);
            Assert.True(moved.HasClaudeGatewayBearerToken);

            // Clearing the URL removes it as well, so no dormant credential waits for the next host.
            AppSettingsRoutes.UpdateSettings(client with { ClaudeEnterpriseNetworkProxy = "" }, store);
            Assert.Empty(store.LoadFresh().ClaudeGatewayBearerToken);

            // A replacement in the same save belongs to the new gateway; scheme and port are part of it.
            AppSettingsRoutes.UpdateSettings(client with
            {
                ClaudeEnterpriseNetworkProxy = "https://api.anthropic.com", ClaudeGatewayBearerToken = "sk-ant-key"
            }, store);
            Assert.Equal("sk-ant-key", store.LoadFresh().ClaudeGatewayBearerToken);
            AppSettingsRoutes.UpdateSettings(client with { ClaudeEnterpriseNetworkProxy = "https://api.anthropic.com:8443" }, store);
            Assert.Empty(store.LoadFresh().ClaudeGatewayBearerToken);
            AppSettingsRoutes.UpdateSettings(client with
            {
                ClaudeEnterpriseNetworkProxy = "https://api.anthropic.com", ClaudeGatewayBearerToken = "sk-ant-key"
            }, store);
            AppSettingsRoutes.UpdateSettings(client with { ClaudeEnterpriseNetworkProxy = "http://api.anthropic.com" }, store);
            Assert.Empty(store.LoadFresh().ClaudeGatewayBearerToken);

            Assert.True(AppSettingsRoutes.SameGatewayOrigin("", " "));
            Assert.False(AppSettingsRoutes.SameGatewayOrigin("", "https://gateway.example/v1"));
            Assert.False(AppSettingsRoutes.SameGatewayOrigin("not a url", "https://gateway.example/v1"));
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
    public void CodexApiAuthIsDerivedFromTheCredentialAndAHiddenHeaderNeverBlocksASave()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-enterprise-derived-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings());
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            var client = read with
            {
                CodexEnterpriseNetworkProxy = null, ClaudeEnterpriseNetworkProxy = null,
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null
            };

            AppSettingsRoutes.UpdateSettings(client with
            {
                CodexEnterpriseNetworkProxy = "https://genai.corp.example/v1", CodexGatewayBearerToken = "corp-token"
            }, store);
            Assert.Equal("subscription", store.LoadFresh().CodexLlmProxyMode);
            Assert.Equal("api", LlmProxySettingsService.Resolve(store.LoadFresh()).CodexLlmProxyMode);
            // Removing the credential returns a ChatGPT-subscription user to their own login.
            AppSettingsRoutes.UpdateSettings(client with { ClearCodexGatewayBearerToken = true }, store);
            Assert.Equal("subscription", LlmProxySettingsService.Resolve(store.LoadFresh()).CodexLlmProxyMode);

            // Bearer mode with a reserved or half-typed name left in the hidden header field: the
            // save (here a theme change) succeeds and the stored header is untouched.
            var saved = AppSettingsRoutes.UpdateSettings(client with
            {
                UseVsCodeTheme = !read.UseVsCodeTheme,
                CodexGatewayAuthMode = "bearer", CodexGatewayApiKeyHeader = "Host",
                ClaudeGatewayAuthMode = "bearer", ClaudeGatewayApiKeyHeader = "x-half typed"
            }, store);
            Assert.Equal(!read.UseVsCodeTheme, saved.UseVsCodeTheme);
            Assert.Equal("api-key", store.LoadFresh().CodexGatewayApiKeyHeader);
            Assert.Equal("api-key", store.LoadFresh().ClaudeGatewayApiKeyHeader);
            // In API-key mode the header is used, so it is still validated.
            Assert.Throws<ArgumentException>(() => AppSettingsRoutes.UpdateSettings(client with
            {
                CodexGatewayAuthMode = "api-key", CodexGatewayApiKeyHeader = "Host"
            }, store));
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

    [Fact]
    public void AnthropicApiKeySavesInOneRequestAndTheToolSearchSwitchRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-enterprise-tool-search-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings());
            var read = AppSettingsRoutes.UpdateComputerName(new UpdateComputerNameDto("Fixture"), store);
            // Absent from older settings files: off, so an unknown gateway is never broken by default.
            Assert.False(read.ClaudeGatewaySupportsToolSearch);

            // What the Settings "Anthropic API key" choice posts: an explicit URL, so the
            // credential stays bound to a saved gateway and the change still needs presence.
            var anthropicKey = read with
            {
                ClaudeEnterpriseNetworkProxy = "https://api.anthropic.com",
                ClaudeGatewayAuthMode = "api-key",
                ClaudeGatewayApiKeyHeader = "x-api-key",
                ClaudeGatewayBearerToken = "sk-ant-secret"
            };
            Assert.True(AppSettingsRoutes.RequiresPresence(anthropicKey, store.LoadFresh()));
            var saved = AppSettingsRoutes.UpdateSettings(anthropicKey, store);
            Assert.Equal("https://api.anthropic.com", saved.ClaudeEnterpriseNetworkProxy);
            Assert.Equal("api-key", saved.ClaudeGatewayAuthMode);
            Assert.Equal("x-api-key", saved.ClaudeGatewayApiKeyHeader);
            Assert.True(saved.HasClaudeGatewayBearerToken);
            Assert.Equal("sk-ant-secret", store.LoadFresh().ClaudeGatewayBearerToken);

            // An older client omits the switch and every gateway field: all of them are preserved.
            var oldClient = read with
            {
                CodexEnterpriseNetworkProxy = null, ClaudeEnterpriseNetworkProxy = null,
                OpenCodeEnterpriseNetworkProxy = null, GrokEnterpriseNetworkProxy = null,
                CodexGatewayAuthMode = null, CodexGatewayApiKeyHeader = null,
                ClaudeGatewayAuthMode = null, ClaudeGatewayApiKeyHeader = null,
                ClaudeGatewaySupportsToolSearch = null
            };
            var on = oldClient with { ClaudeGatewaySupportsToolSearch = true };
            // The switch is not routing: flipping it needs no PIN or dashboard code.
            Assert.False(AppSettingsRoutes.RequiresPresence(on, store.LoadFresh()));
            Assert.True(AppSettingsRoutes.UpdateSettings(on, store).ClaudeGatewaySupportsToolSearch);
            Assert.True(store.LoadFresh().ClaudeGatewaySupportsToolSearch);
            var preserved = AppSettingsRoutes.UpdateSettings(oldClient, store);
            Assert.True(preserved.ClaudeGatewaySupportsToolSearch);
            Assert.Equal("https://api.anthropic.com", preserved.ClaudeEnterpriseNetworkProxy);
            Assert.Equal("x-api-key", preserved.ClaudeGatewayApiKeyHeader);
            Assert.False(AppSettingsRoutes.UpdateSettings(oldClient with { ClaudeGatewaySupportsToolSearch = false }, store)
                .ClaudeGatewaySupportsToolSearch);
            Assert.False(store.LoadFresh().ClaudeGatewaySupportsToolSearch);
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
}
