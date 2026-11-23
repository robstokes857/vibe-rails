using System.Text.Json.Nodes;
using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    private const string VibeRailsMcpApproval = "mcp_servers.viberails-mcp.default_tools_approval_mode=\"approve\"";
    private const string AntigravityVibeRailsMcpSettings = "{\"permissions\":{\"allow\":[\"mcp(viberails-mcp/*)\"]}}";

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("saved-codex", false)]
    [InlineData("saved-codex", true)]
    public async Task CodexLaunch_ApprovesVibeRailsMcpByDefaultWithoutPersistingPermissions(string? envName, bool proxyEnabled)
    {
        Mock<IFileService> files = null!;
        var service = CreateService(codexLlmProxyEnabled: proxyEnabled, configureFiles: mock => files = mock);
        var prepared = await service.PrepareSessionAsync(LLM.Codex, envName, null);

        Assert.Equal(new[] { "--config", VibeRailsMcpApproval }, prepared.Argv!.Take(2));
        Assert.Single(prepared.Argv!, arg => arg == VibeRailsMcpApproval);
        Assert.Contains("default_tools_approval_mode", prepared.LaunchCommand);
        Assert.DoesNotContain(prepared.SetupCommands, command => command.Contains("approval_mode"));
        Assert.DoesNotContain(prepared.Argv!, arg => arg.StartsWith("approval_policy=")
            || arg.StartsWith("sandbox_mode=") || arg.Contains("bypass")
            || arg.Contains(".tools.") || arg.Contains('*'));
        files.Verify(file => file.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CodexLaunch_PreservesExplicitApprovalOverridesAndPromptDelimiter()
    {
        string[] customArgs = [
            "--config", "mcp_servers.viberails-mcp.default_tools_approval_mode=\"prompt\"",
            "--config", "mcp_servers.viberails-mcp.tools.add_jira_comment.approval_mode=\"prompt\"",
            "--config", "mcp_servers.other.default_tools_approval_mode=\"prompt\"",
            "--config", "mcp_servers.viberails-mcp.disabled_tools=[\"create_session_share_link\"]",
            "--config", "approval_policy=\"on-request\"", "--config", "sandbox_mode=\"workspace-write\"",
            "--", "Inspect the project"
        ];
        var prepared = await CreateService().PrepareSessionAsync(LLM.Codex, "saved-codex", customArgs);

        // Codex applies repeated config arguments in order: the caller's default follows ours.
        Assert.Equal(customArgs, prepared.Argv!.Skip(2).Take(customArgs.Length));
        Assert.Single(prepared.Argv!, arg => arg == "--");
        Assert.Equal(new[] { "--", "Inspect the project" }, prepared.Argv!.TakeLast(2));
        Assert.Contains("default_tools_approval_mode", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("saved-antigravity")]
    public async Task AntigravityLaunch_ApprovesVibeRailsMcpByDefaultWithoutPersistingPermissions(string? envName)
    {
        Mock<IFileService> files = null!;
        var service = CreateService(configureFiles: mock => files = mock);
        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, envName, null);

        Assert.Equal(["--settings", AntigravityVibeRailsMcpSettings], prepared.Argv);
        Assert.Contains("--settings", prepared.LaunchCommand);
        Assert.Contains("mcp(viberails-mcp/*)", prepared.LaunchCommand);
        Assert.DoesNotContain(prepared.SetupCommands, command => command.Contains("--settings"));
        Assert.DoesNotContain(prepared.Argv!, arg => arg == "--dangerously-skip-permissions");
        files.Verify(file => file.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AntigravityLaunch_MergesExistingUserAllowsAndPreservesDenyAsk()
    {
        const string existing = "{\"model\":\"gemini-4-neon\",\"permissions\":{\"allow\":[\"command(git)\",\"read_url(example.com)\"],\"deny\":[\"mcp(viberails-mcp/create_session_share_link)\"],\"ask\":[\"mcp(viberails-mcp/add_jira_comment)\"]}}";
        var profile = Path.Combine(Path.GetTempPath(), "viberails-grok-tests");
        var settingsPath = BoardMcpAntigravityAuthorization.ResolveUserSettingsPath(profile);
        var service = CreateService(configureFiles: mock =>
        {
            mock.Setup(f => f.FileExists(settingsPath)).Returns(true);
            mock.Setup(f => f.ReadAllTextAsync(settingsPath, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        });

        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, null, ["--mode", "auto"]);

        Assert.Equal("--settings", prepared.Argv![2]);
        var root = JsonNode.Parse(prepared.Argv[3])!.AsObject();
        Assert.Single(root);
        var permissions = root["permissions"]!.AsObject();
        Assert.Single(permissions);
        Assert.False(permissions.ContainsKey("deny"));
        Assert.False(permissions.ContainsKey("ask"));
        var allows = permissions["allow"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(["command(git)", "read_url(example.com)", "mcp(viberails-mcp/*)"], allows);
    }

    [Theory]
    [InlineData("--settings", "{\"permissions\":{\"ask\":[\"mcp(viberails-mcp/*)\"]}}")]
    [InlineData("--settings={\"agentMode\":\"plan\"}", null)]
    [InlineData("-settings", "custom.json")]
    public async Task AntigravityLaunch_PreservesExplicitSettingsOverrides(string first, string? second)
    {
        string[] customArgs = second is null ? [first] : [first, second];
        var prepared = await CreateService().PrepareSessionAsync(LLM.Antigravity, "saved-antigravity", customArgs);

        Assert.Equal(customArgs, prepared.Argv);
    }

    [Fact]
    public async Task AntigravityLaunch_FailsClosedWhenUserSettingsMalformed()
    {
        var profile = Path.Combine(Path.GetTempPath(), "viberails-grok-tests");
        var settingsPath = BoardMcpAntigravityAuthorization.ResolveUserSettingsPath(profile);
        var service = CreateService(configureFiles: mock =>
        {
            mock.Setup(f => f.FileExists(settingsPath)).Returns(true);
            mock.Setup(f => f.ReadAllTextAsync(settingsPath, It.IsAny<CancellationToken>())).ReturnsAsync("not-json");
        });

        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, null, ["--model", "gemini-4-neon"]);

        Assert.Equal(["--model", "gemini-4-neon"], prepared.Argv);
        Assert.DoesNotContain("--settings", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Glm53)]
    [InlineData(LLM.DeepSeekV4Pro)]
    [InlineData(LLM.KimiK3)]
    [InlineData(LLM.Shell)]
    public async Task OtherCliLaunch_DoesNotReceiveCodexMcpApproval(LLM llm)
    {
        var prepared = await CreateService().PrepareSessionAsync(llm, null, null);
        Assert.DoesNotContain("default_tools_approval_mode", prepared.Command);
        Assert.DoesNotContain(prepared.Argv ?? [], arg => arg.Contains("default_tools_approval_mode"));
    }

    [Theory]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Glm53)]
    [InlineData(LLM.DeepSeekV4Pro)]
    [InlineData(LLM.KimiK3)]
    [InlineData(LLM.Shell)]
    public async Task OtherCliLaunch_DoesNotReceiveAntigravityMcpApproval(LLM llm)
    {
        var prepared = await CreateService().PrepareSessionAsync(llm, null, null);
        Assert.DoesNotContain("mcp(viberails-mcp/*)", prepared.Command);
        Assert.DoesNotContain(prepared.Argv ?? [], arg => arg == "--settings" || arg.Contains("mcp(viberails-mcp/*)"));
    }
}
