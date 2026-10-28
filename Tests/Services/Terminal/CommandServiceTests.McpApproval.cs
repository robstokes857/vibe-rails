using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    private const string VibeRailsMcpApproval = "mcp_servers.viberails-mcp.default_tools_approval_mode=\"approve\"";

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
}
