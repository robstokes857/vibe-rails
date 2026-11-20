using System.Text.Json;
using VibeRails.Routes;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Account settings saves only the API key (settings-controller.js, saveAccountKey). It repeats
/// RemoteAccess and UseVsCodeTheme, which the route always writes, and omits every App settings
/// field. These tests pin the route contract that makes that safe: an omitted field is unchanged.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class AppSettingsAccountKeyTests
{
    private const string AccountKeyOnlyJson =
        """{"presenceProof":null,"remoteAccess":true,"apiKey":"replacement-key","useVsCodeTheme":true,"mcpEnabled":true,"clearApiKey":false,"dataExportOptIn":true}""";

    [Fact]
    public void KeyOnlySave_ReplacesTheKeyAndLeavesAppSettingsUnchanged()
    {
        WithStore(store =>
        {
            store.Save(SavedSettings());
            var request = JsonSerializer.Deserialize(AccountKeyOnlyJson, AppJsonSerializerContext.Default.AppSettingsDto)!;

            Assert.True(AppSettingsRoutes.RequiresPresence(request, store.LoadFresh()));
            AppSettingsRoutes.UpdateSettings(request, store);

            var saved = store.LoadFresh();
            Assert.Equal("replacement-key", saved.ApiKey);
            Assert.True(saved.RemoteAccess);
            Assert.True(saved.UseVsCodeTheme);
            Assert.Equal("Work laptop", saved.ComputerName);
            Assert.Equal("api", saved.CodexLlmProxyMode);
            Assert.Equal("api", saved.GrokLlmProxyMode);
            Assert.Equal("https://gateway.example.test/v1", saved.CodexEnterpriseNetworkProxy);
            Assert.Equal("gateway-token", saved.CodexGatewayBearerToken);
            Assert.True(saved.ClaudeGatewaySupportsToolSearch);
            Assert.False(saved.ClaudeTokenSaverEnabled);
            Assert.True(saved.VsCodeSpellCheck);
            Assert.False(saved.CreateVibeStoryTracking);
        });
    }

    [Fact]
    public void KeyOnlySave_WithTheSavedSwitchesAndNoKeyChange_NeedsNoPresence()
    {
        WithStore(store =>
        {
            store.Save(SavedSettings());
            var request = JsonSerializer.Deserialize(AccountKeyOnlyJson, AppJsonSerializerContext.Default.AppSettingsDto)!
                with { ApiKey = "" };

            Assert.False(AppSettingsRoutes.RequiresPresence(request, store.LoadFresh()));
            AppSettingsRoutes.UpdateSettings(request, store);
            Assert.Equal("saved-key", store.LoadFresh().ApiKey);
        });
    }

    private static Settings SavedSettings() => new()
    {
        ApiKey = "saved-key",
        RemoteAccess = true,
        PinHash = "hash",
        PinSalt = "salt",
        UseVsCodeTheme = true,
        ComputerName = "Work laptop",
        CodexLlmProxyMode = "api",
        GrokLlmProxyMode = "api",
        CodexEnterpriseNetworkProxy = "https://gateway.example.test/v1",
        CodexGatewayBearerToken = "gateway-token",
        ClaudeGatewaySupportsToolSearch = true,
        ClaudeTokenSaverEnabled = false,
        VsCodeSpellCheck = true,
        CreateVibeStoryTracking = false
    };

    private static void WithStore(Action<SettingsFile> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-account-key-settings-{Guid.NewGuid():N}");
        var remote = ParserConfigs.GetRemoteAccess();
        var key = ParserConfigs.GetApiKey();
        var theme = ParserConfigs.GetUseVsCodeTheme();
        var mcp = ParserConfigs.GetMcpEnabled();
        var relay = ParserConfigs.GetRouteThroughVibeRailsAi();
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            test(store);
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
