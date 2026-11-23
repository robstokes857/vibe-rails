using System.Text.Json.Nodes;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    [Fact]
    public async Task PrepareSession_Antigravity_WithoutSavedApiKey_DoesNotInjectApiKeyOrModelProvider()
    {
        var service = CreateService(storyTrackingSettings: () => new Settings { CreateVibeStoryTracking = false });

        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey(BoardMcpAntigravityAuthorization.GeminiApiKeyVariable));
        var settingsIndex = prepared.Argv!.ToList().IndexOf("--settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < prepared.Argv!.Count);
        var settingsObj = JsonNode.Parse(prepared.Argv![settingsIndex + 1])!.AsObject();
        Assert.False(settingsObj.ContainsKey("modelProvider"));
        var allows = settingsObj["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["mcp(viberails-mcp/*)"], allows);
    }

    [Fact]
    public async Task PrepareSession_Antigravity_WithSavedApiKey_SetsEnvAndSessionOnlyModelProviderWithoutLeakingKey()
    {
        const string secret = "AIzaSyTestAntigravitySecret123";
        var service = CreateService(storyTrackingSettings: () => new Settings
        {
            CreateVibeStoryTracking = false,
            AntigravityApiKey = secret
        });

        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, envName: null, extraArgs: ["--model", "gemini-4-neon"]);

        Assert.Equal(secret, prepared.Environment[BoardMcpAntigravityAuthorization.GeminiApiKeyVariable]);
        Assert.DoesNotContain(secret, prepared.Command);
        Assert.DoesNotContain(secret, prepared.LaunchCommand);
        Assert.DoesNotContain(prepared.SetupCommands, cmd => cmd.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(prepared.Argv!, arg => arg.Contains(secret, StringComparison.Ordinal));

        var settingsIndex = prepared.Argv!.ToList().IndexOf("--settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < prepared.Argv!.Count);
        var settingsObj = JsonNode.Parse(prepared.Argv![settingsIndex + 1])!.AsObject();
        Assert.Equal("gemini", settingsObj["modelProvider"]!.GetValue<string>());
        var allows = settingsObj["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["mcp(viberails-mcp/*)"], allows);
        Assert.Equal(2, settingsObj.Count);
        Assert.Contains("--settings", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Antigravity_WithSavedApiKeyAndBoardTools_MergesIntoOneSettingsObject()
    {
        const string secret = "AIzaSyBoardSecret456";
        var service = CreateService(storyTrackingSettings: () => new Settings
        {
            CreateVibeStoryTracking = false,
            AntigravityApiKey = secret
        });

        var prepared = await service.PrepareSessionAsync(
            LLM.Antigravity, envName: null, extraArgs: ["--mode", "auto"], authorizeBoardTools: true);

        Assert.Equal(secret, prepared.Environment[BoardMcpAntigravityAuthorization.GeminiApiKeyVariable]);
        Assert.DoesNotContain(secret, prepared.Command);

        var argv = prepared.Argv!.ToList();
        Assert.Equal(1, argv.Count(arg => arg == "--settings"));
        var settingsIndex = argv.IndexOf("--settings");
        var settingsObj = JsonNode.Parse(argv[settingsIndex + 1])!.AsObject();
        Assert.Equal("gemini", settingsObj["modelProvider"]!.GetValue<string>());
        var allows = settingsObj["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("mcp(viberails-mcp/*)", allows);
        Assert.Contains("mcp(viberails-mcp/get_board_card)", allows);
    }

    [Theory]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Shell)]
    public async Task PrepareSession_OtherProviders_NeverReceiveAntigravityApiKeyOrSettingsFlag(LLM llm)
    {
        const string secret = "AIzaSyAntigravityOnly789";
        var service = CreateService(storyTrackingSettings: () => new Settings
        {
            CreateVibeStoryTracking = false,
            AntigravityApiKey = secret
        });

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey(BoardMcpAntigravityAuthorization.GeminiApiKeyVariable));
        Assert.DoesNotContain(secret, prepared.Environment.Values);
        Assert.DoesNotContain("--settings", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("AIza bad space")]
    [InlineData("AIza\nnewline")]
    public async Task PrepareSession_Antigravity_InvalidHandEditedKeyIsIgnoredSafely(string stored)
    {
        var service = CreateService(storyTrackingSettings: () => new Settings
        {
            CreateVibeStoryTracking = false,
            AntigravityApiKey = stored
        });

        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey(BoardMcpAntigravityAuthorization.GeminiApiKeyVariable));
        var settingsIndex = prepared.Argv!.ToList().IndexOf("--settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < prepared.Argv!.Count);
        var settingsObj = JsonNode.Parse(prepared.Argv![settingsIndex + 1])!.AsObject();
        Assert.False(settingsObj.ContainsKey("modelProvider"));
    }
}
