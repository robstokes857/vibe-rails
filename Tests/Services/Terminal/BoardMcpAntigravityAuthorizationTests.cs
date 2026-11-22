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
