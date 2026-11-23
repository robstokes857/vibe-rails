using System.Text.Json.Nodes;
using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class BoardMcpAntigravityAuthorizationTests
{
    [Fact]
    public async Task MissingSettingsFile_GrantsOnlyExactReviewedBoardTools()
    {
        var files = CreateFileService(settingsContent: null);

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon", "--effort", "xhigh"], files.Object,
            TestContext.Current.CancellationToken);

        Assert.Equal(["--model", "gemini-4-neon", "--effort", "xhigh"], args[..4]);
        Assert.Equal("--settings", args[4]);
        var root = JsonNode.Parse(args[5])!.AsObject();
        var permissions = root["permissions"]!.AsObject();
        Assert.Single(root);
        Assert.Single(permissions);
        var allows = permissions["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(BoardMcpAuthorization.ToolNames.Select(tool => $"mcp(viberails-mcp/{tool})"), allows);
        Assert.DoesNotContain(allows, rule => rule.Contains('*'));
        Assert.DoesNotContain("mcp(viberails-mcp/search_history)", allows);
        Assert.DoesNotContain("mcp(viberails-mcp/request_review)", allows);
        Assert.DoesNotContain("mcp(viberails-mcp/create_session_share_link)", allows);
        files.Verify(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingSettingsFile_MergesAllowsPreservesOrderAndOmitsDenyAsk()
    {
        const string existing = "\uFEFF{\"model\":\"Gemini 4 Neon (XHigh)\",\"permissions\":{\"allow\":[\"command(git)\",\"mcp(viberails-mcp/get_board_card)\",\"read_url(example.com)\"],\"deny\":[\"command(rm -rf)\"],\"ask\":[\"mcp(viberails-mcp/update_board_card)\"]}}";
        var files = CreateFileService(existing);

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, ["--mode", "auto"], files.Object, TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[2]);
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Single(root);
        var permissions = root["permissions"]!.AsObject();
        Assert.Single(permissions);
        var allows = permissions["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(["command(git)", "mcp(viberails-mcp/get_board_card)", "read_url(example.com)"], allows[..3]);
        Assert.Equal(BoardMcpAuthorization.ToolNames.Count + 2, allows.Length);
        Assert.Single(allows, rule => rule == "mcp(viberails-mcp/get_board_card)");
        Assert.Contains("mcp(viberails-mcp/list_boards)", allows);
        Assert.Contains("mcp(viberails-mcp/update_board_card)", allows);
    }

    [Theory]
    [InlineData("--settings", "{\"agentMode\":\"plan\"}")]
    [InlineData("--settings={\"agentMode\":\"plan\"}", null)]
    [InlineData("-settings", "custom.json")]
    [InlineData("-settings=custom.json", null)]
    public async Task ExplicitSettingsFlag_IsPreservedWithoutOverwriting(string first, string? second)
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = second is null ? [first] : [first, second];

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, input, files.Object, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task GrantsInsertBeforeEndOfOptionsDelimiter()
    {
        var files = CreateFileService(settingsContent: null);

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon", "--", "--settings"], files.Object,
            TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[2]);
        Assert.Equal("--", args[4]);
        Assert.Equal("--settings", args[5]);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"permissions\":[]}")]
    [InlineData("{\"permissions\":{\"allow\":\"command(git)\"}}")]
    [InlineData("{\"permissions\":{\"allow\":[123]}}")]
    public async Task MalformedSettingsFile_FailsClosedWithoutClobberingUserPermissions(string content)
    {
        var files = CreateFileService(content);
        string[] input = ["--model", "gemini-4-neon"];

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, input, files.Object, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task UnreadableSettingsFile_FailsClosedWithoutThrowing()
    {
        var files = new Mock<IFileService>();
        files.Setup(f => f.GetUserProfilePath()).Returns(@"C:\users\tester");
        files.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        files.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));

        string[] input = ["--model", "gemini-4-neon"];
        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            LLM.Antigravity, input, files.Object, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public void OversizedMergedAllows_FailsClosedToRespectCommandLineBounds()
    {
        var hugeAllowList = string.Join(',', Enumerable.Range(0, 400).Select(i => $"\"command(tool-{i:D4}-very-long-rule-name)\""));
        var existing = $"{{\"permissions\":{{\"allow\":[{hugeAllowList}]}}}}";

        Assert.False(BoardMcpAntigravityAuthorization.TryBuildSettingsOverride(existing, out _));
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Shell)]
    public async Task OtherProvidersRemainUntouched(LLM llm)
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = ["--model", "chosen"];

        var args = await BoardMcpAntigravityAuthorization.AppendArgumentsAsync(
            llm, input, files.Object, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task GeminiApiKeyWithoutBoardTools_InjectsModelProviderBeforeDelimiter()
    {
        var files = CreateFileService(settingsContent: null);

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon", "--", "positional"], files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[2]);
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Single(root);
        Assert.Equal("gemini", root["modelProvider"]!.GetValue<string>());
        Assert.Equal("--", args[4]);
        Assert.Equal("positional", args[5]);
    }

    [Fact]
    public async Task GeminiApiKeyWithMalformedUserSettings_FailsClosedOnBoardGrantsButKeepsModelProvider()
    {
        var files = CreateFileService("not-json");

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon"], files.Object,
            authorizeBoardTools: true, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[2]);
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Single(root);
        Assert.Equal("gemini", root["modelProvider"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--settings", "{\"agentMode\":\"plan\"}", null)]
    [InlineData("--settings={\"agentMode\":\"plan\"}", null, "--settings=")]
    public async Task GeminiApiKeyMergesIntoExplicitInlineSettingsWhenModelProviderAbsent(string first, string? second, string? prefix)
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = second is null ? [first] : [first, second];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal(input.Length, args.Length);
        var payload = prefix is null ? args[1] : args[0][prefix.Length..];
        var root = JsonNode.Parse(payload)!.AsObject();
        Assert.Equal("plan", root["agentMode"]!.GetValue<string>());
        Assert.Equal("gemini", root["modelProvider"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--settings", "{\"modelProvider\":\"\"}")]
    [InlineData("--settings", "custom.json")]
    [InlineData("-settings=custom.json", null)]
    public async Task GeminiApiKeyPreservesExplicitModelProviderOrFileSettings(string first, string? second)
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = second is null ? [first] : [first, second];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task GeminiApiKeyMergesIntoLastExplicitSettingsOccurrence()
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = ["--settings", "{\"agentMode\":\"plan\"}", "--settings={\"verbosity\":\"high\"}"];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        // First occurrence stays untouched; last occurrence (which agy uses) gets modelProvider.
        Assert.Equal("{\"agentMode\":\"plan\"}", args[1]);
        Assert.StartsWith("--settings=", args[2]);
        var lastObj = JsonNode.Parse(args[2]["--settings=".Length..])!.AsObject();
        Assert.Equal("high", lastObj["verbosity"]!.GetValue<string>());
        Assert.Equal("gemini", lastObj["modelProvider"]!.GetValue<string>());
    }

    [Fact]
    public async Task GeminiApiKeyWithFileThenInlineSettings_MergesLastInlineOccurrence()
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = ["--settings=custom.json", "--settings", "{\"agentMode\":\"auto\"}"];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal("--settings=custom.json", args[0]);
        var lastObj = JsonNode.Parse(args[2])!.AsObject();
        Assert.Equal("auto", lastObj["agentMode"]!.GetValue<string>());
        Assert.Equal("gemini", lastObj["modelProvider"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--settings", "{\"agentMode\":\"plan\"}", "--settings=custom.json")]
    [InlineData("--settings", "{\"agentMode\":\"plan\"}", "--settings={\"modelProvider\":\"\"}")]
    [InlineData("--settings", "{\"agentMode\":\"plan\"}", "--settings")]
    public async Task GeminiApiKeyWithOverridingFinalSettingsFlag_LeavesArgumentsUnchanged(string a, string b, string c)
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = [a, b, c];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task GeminiApiKeyWithDuplicateJsonProperties_MergesWithoutThrowing()
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = ["--settings", "{\"agentMode\":\"plan\",\"agentMode\":\"auto\"}"];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        using var doc = System.Text.Json.JsonDocument.Parse(args[1]);
        Assert.Equal("auto", doc.RootElement.GetProperty("agentMode").GetString());
        Assert.Equal("gemini", doc.RootElement.GetProperty("modelProvider").GetString());
    }

    [Fact]
    public async Task GeminiApiKeyWithDuplicateJsonPropertiesAndExplicitModelProvider_PreservesWithoutThrowing()
    {
        var files = CreateFileService(settingsContent: null);
        string[] input = ["--settings", "{\"agentMode\":\"plan\",\"agentMode\":\"auto\",\"modelProvider\":\"\"}"];

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, input, files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, TestContext.Current.CancellationToken);

        Assert.Equal(input, args);
    }

    [Fact]
    public async Task ApproveVibeRailsMcp_MissingSettingsFile_GrantsServerWildcardOnly()
    {
        var files = CreateFileService(settingsContent: null);

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon"], files.Object,
            authorizeBoardTools: false, useGeminiApiKey: false, approveVibeRailsMcp: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(["--model", "gemini-4-neon"], args[..2]);
        Assert.Equal("--settings", args[2]);
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Single(root);
        var permissions = root["permissions"]!.AsObject();
        Assert.Single(permissions);
        var allows = permissions["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(["mcp(viberails-mcp/*)"], allows);
        files.Verify(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApproveVibeRailsMcp_ExistingSettingsFile_MergesDeduplicatesAndOmitsDenyAsk()
    {
        const string existing = "{\"permissions\":{\"allow\":[\"command(git)\",\"mcp(viberails-mcp/*)\",\"read_url(example.com)\"],\"deny\":[\"mcp(viberails-mcp/create_session_share_link)\"],\"ask\":[\"mcp(viberails-mcp/add_jira_comment)\"]}}";
        var files = CreateFileService(existing);

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, [], files.Object,
            authorizeBoardTools: false, useGeminiApiKey: false, approveVibeRailsMcp: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[0]);
        var root = JsonNode.Parse(args[1])!.AsObject();
        var permissions = root["permissions"]!.AsObject();
        Assert.Single(permissions);
        var allows = permissions["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(["command(git)", "mcp(viberails-mcp/*)", "read_url(example.com)"], allows);
    }

    [Fact]
    public async Task ApproveVibeRailsMcp_CombinesWithBoardToolsAndGeminiApiKeyInOneSettingsObject()
    {
        var files = CreateFileService("{\"permissions\":{\"allow\":[\"command(git)\"]}}");

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, ["--mode", "auto"], files.Object,
            authorizeBoardTools: true, useGeminiApiKey: true, approveVibeRailsMcp: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, args.Count(arg => arg == "--settings"));
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Equal("gemini", root["modelProvider"]!.GetValue<string>());
        var allows = root["permissions"]!["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal("command(git)", allows[0]);
        Assert.Equal("mcp(viberails-mcp/*)", allows[1]);
        Assert.Equal(BoardMcpAuthorization.ToolNames.Select(tool => $"mcp(viberails-mcp/{tool})"), allows[2..]);
    }

    [Fact]
    public async Task ApproveVibeRailsMcp_MalformedSettingsWithGeminiApiKey_FailsClosedOnMcpButKeepsModelProvider()
    {
        var files = CreateFileService("not-json");

        var args = await BoardMcpAntigravityAuthorization.AppendSessionSettingsAsync(
            LLM.Antigravity, ["--model", "gemini-4-neon"], files.Object,
            authorizeBoardTools: false, useGeminiApiKey: true, approveVibeRailsMcp: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("--settings", args[2]);
        var root = JsonNode.Parse(args[3])!.AsObject();
        Assert.Single(root);
        Assert.Equal("gemini", root["modelProvider"]!.GetValue<string>());
    }

    private static Mock<IFileService> CreateFileService(string? settingsContent)
    {
        var profile = Path.Combine(Path.GetTempPath(), "viberails-agy-auth-test");
        var settingsPath = BoardMcpAntigravityAuthorization.ResolveUserSettingsPath(profile);
        var files = new Mock<IFileService>();
        files.Setup(f => f.GetUserProfilePath()).Returns(profile);
        files.Setup(f => f.FileExists(settingsPath)).Returns(settingsContent is not null);
        files.Setup(f => f.ReadAllTextAsync(settingsPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(settingsContent ?? string.Empty);
        return files;
    }
}
